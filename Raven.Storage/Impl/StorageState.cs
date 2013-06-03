﻿namespace Raven.Storage.Impl
{
	using System;
	using System.Diagnostics;
	using System.Text;
	using System.Threading.Tasks;

	using Raven.Storage.Building;
	using Raven.Storage.Impl.Caching;
	using Raven.Storage.Impl.Streams;
	using Raven.Storage.Memtable;

	public class StorageState : IDisposable
	{
		public MemTable MemTable;
		public volatile MemTable ImmutableMemTable;
		public volatile bool BackgroundCompactionScheduled;
		public volatile Task BackgroundTask = Task.FromResult<object>(null);
		public volatile bool ShuttingDown;

		public LogWriter LogWriter;

		public LogWriter DescriptorLogWriter;

		public AsyncLock Lock;
		public VersionSet VersionSet { get; private set; }
		public StorageOptions Options;
		public FileSystem FileSystem;
		public string DatabaseName;
		public ulong LogFileNumber { get; private set; }

		public CompactionStats[] CompactionStats = new CompactionStats[Config.NumberOfLevels];

		public ManualCompaction ManualCompaction { get; set; }

		public TableCache TableCache { get; private set; }

		public StorageState()
		{
			this.TableCache = new TableCache(this);
			this.VersionSet = new VersionSet(this.Options, this.TableCache);
		}

		public void CreateNewLog()
		{
			var newFileNumber = VersionSet.NewFileNumber();
			try
			{
				var file = FileSystem.NewWritable(DatabaseName, newFileNumber, "log");
				LogWriter = new LogWriter(file, Options.BufferPool);
				LogFileNumber = newFileNumber;
			}
			catch (Exception)
			{
				// Avoid chewing through file number space in a tight loop.
				VersionSet.ReuseFileNumber(newFileNumber);
				throw;
			}
		}

		public void LogAndApply(VersionEdit edit)
		{
			string newManifestFile = null;

			try
			{
				if (!edit.HasLogNumber)
					edit.SetLogNumber(VersionSet.LogNumber);
				else if (edit.LogNumber < VersionSet.LogNumber || edit.LogNumber >= VersionSet.NextFileNumber)
					throw new InvalidOperationException("LogNumber");

				if (!edit.HasPrevLogNumber)
					edit.SetPrevLogNumber(VersionSet.PrevLogNumber);

				edit.SetNextFile(VersionSet.NextFileNumber);
				edit.SetLastSequence(VersionSet.LastSequence);

				var version = new Version(Options, VersionSet);

				var builder = new Builder(Options, VersionSet, VersionSet.Current);
				builder.Apply(edit);
				builder.SaveTo(version);

				Version.Finalize(version);

				// Initialize new descriptor log file if necessary by creating
				// a temporary file that contains a snapshot of the current version.

				if (DescriptorLogWriter == null)
				{
					// No reason to unlock *mu here since we only hit this path in the
					// first call to LogAndApply (when opening the database).

					newManifestFile = FileSystem.DescriptorFileName(this.DatabaseName, VersionSet.ManifestFileNumber);
					edit.SetNextFile(VersionSet.NextFileNumber);
					var descriptorFile = FileSystem.NewWritable(newManifestFile);

					DescriptorLogWriter = new LogWriter(descriptorFile, this.Options.BufferPool);
					Snapshot.Write(DescriptorLogWriter, Options, VersionSet);
				}

				// Unlock during expensive MANIFEST log write
				//mu->Unlock();

				// Write new record to MANIFEST log

				edit.EncodeTo(DescriptorLogWriter);
				DescriptorLogWriter.Flush();

				//if (!s.ok()) {
				//	Log(options_->info_log, "MANIFEST write: %s\n", s.ToString().c_str());
				//	if (ManifestContains(record)) {
				//	  Log(options_->info_log,
				//		  "MANIFEST contains log record despite error; advancing to new "
				//		  "version to prevent mismatch between in-memory and logged state");
				//	  s = Status::OK();
				//	}
				//}

				// If we just created a new descriptor file, install it by writing a
				// new CURRENT file that points to it.
				if (!string.IsNullOrEmpty(newManifestFile))
				{
					this.SetCurrentFile(this.DatabaseName, VersionSet.ManifestFileNumber);
					// No need to double-check MANIFEST in case of error since it
					// will be discarded below.
				}

				//mu->Lock();

				// Install the new version
				VersionSet.AppendVersion(version);
				VersionSet.SetLogNumber(edit.LogNumber);
				VersionSet.SetPrevLogNumber(edit.PrevLogNumber);
			}
			catch (Exception)
			{
				if (!string.IsNullOrEmpty(newManifestFile))
				{
					if (DescriptorLogWriter != null)
					{
						DescriptorLogWriter.Dispose();
						DescriptorLogWriter = null;
					}

					FileSystem.DeleteFile(newManifestFile);
				}
			}
		}

		private void SetCurrentFile(string databaseName, ulong descriptorNumber)
		{
			var manifest = FileSystem.DescriptorFileName(databaseName, descriptorNumber);
			var contents = manifest.Substring(databaseName.Length + 1, manifest.Length) + "\n";

			var temporaryFileName = FileSystem.GetFileName(databaseName, descriptorNumber, Constants.Files.Extensions.TempFile);

			using (var stream = FileSystem.NewWritable(temporaryFileName))
			{
				var encodedContents = Encoding.UTF8.GetBytes(contents);
				stream.Write(encodedContents, 0, encodedContents.Length);
			}

			FileSystem.RenameFile(temporaryFileName, FileSystem.GetCurrentFileName(databaseName));
		}

		public void Dispose()
		{
			if (LogWriter != null)
				LogWriter.Dispose();
			if (FileSystem != null)
				FileSystem.Dispose();
			if (MemTable != null)
				MemTable.Dispose();
			if (ImmutableMemTable != null)
				ImmutableMemTable.Dispose();
		}

		public FileMetadata BuildTable(MemTable memTable, ulong fileNumber)
		{
			TableBuilder builder = null;
			var meta = new FileMetadata
						   {
							   FileNumber = fileNumber
						   };

			var tableFileName = FileSystem.GetFileName(DatabaseName, fileNumber, Constants.Files.Extensions.TableFile);
			var tempFileName = FileSystem.GetFileName(DatabaseName, fileNumber, Constants.Files.Extensions.TempFile);

			try
			{
				var iterator = memTable.NewIterator();

				if (iterator.IsValid)
				{
					var tableFile = FileSystem.NewWritable(tableFileName);
					var tempFile = FileSystem.NewWritable(tempFileName);
					builder = new TableBuilder(Options, tableFile, () => tempFile);

					meta.SmallestKey = iterator.Key;
					while (iterator.IsValid)
					{
						var key = iterator.Key;
						var stream = iterator.CreateValueStream();

						meta.LargestKey = key;
						builder.Add(key, stream);

						iterator.Next();
					}

					builder.Finish();

					meta.FileSize = builder.FileSize;
					Debug.Assert(meta.FileSize > 0);
				}
			}
			finally
			{
				if (builder != null)
					builder.Dispose();

				if (meta.FileSize == 0)
				{
					FileSystem.DeleteFile(tableFileName);
					FileSystem.DeleteFile(tempFileName);
				}
			}

			return meta;
		}
	}
}
