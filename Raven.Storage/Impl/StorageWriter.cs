﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using Raven.Storage.Memtable;

namespace Raven.Storage.Impl
{
	using System.Threading;


	public class StorageWriter
	{
		private readonly StorageState _state;
		private readonly ConcurrentQueue<OutstandingWrite> _pendingWrites = new ConcurrentQueue<OutstandingWrite>();
		private readonly AsyncMonitor _writeCompletedEvent = new AsyncMonitor();

		private readonly IList<ulong> pendingOutputs = new List<ulong>();

		private class OutstandingWrite
		{
			public WriteBatch Batch { get; private set; }
			public TaskCompletionSource<object> Result { get; private set; }

			public OutstandingWrite(WriteBatch batch)
			{
				Batch = batch;
				Size = batch.Size;
				Result = new TaskCompletionSource<object>();
			}

			public long Size { get; private set; }

			public bool Done()
			{
				var task = Result.Task;
				if (task.IsCompleted)
					return true;
				if (task.IsCanceled || task.IsFaulted)
					task.Wait(); // throws
				return false;
			}
		}

		public StorageWriter(StorageState state)
		{
			_state = state;
		}

		public void Write(WriteBatch batch)
		{
			WriteAsync(batch).Wait();
		}

		public async Task WriteAsync(WriteBatch batch)
		{
			var mine = new OutstandingWrite(batch);
			_pendingWrites.Enqueue(mine);

			while (mine.Done() == false && _pendingWrites.Peek() != mine)
			{
				await _writeCompletedEvent.WaitAsync();
			}

			if (mine.Done())
				return;

			using (var locker = await _state.Lock.LockAsync())
			{
				try
				{
					if (mine.Done())
						return;

					await MakeRoomForWrite(force: false, lockScope: locker);

					var lastSequence = _state.VersionSet.LastSequence;

					var list = BuildBatchGroup(mine);

					var currentSequence = lastSequence + 1;

					lastSequence += (ulong)list.Count;

					// Add to log and apply to memtable.  We can release the lock
					// during this phase since mine is currently responsible for logging
					// and protects against concurrent loggers and concurrent writes
					// into the mem table.

					locker.Exit();
					{
						foreach (var write in list)
						{
							write.Batch.Prepare(_state.MemTable);
						}

						await WriteBatch.WriteToLog(list.Select(x => x.Batch).ToArray(), currentSequence, _state);

						foreach (var write in list)
						{
							write.Batch.Apply(_state.MemTable, currentSequence);
						}
					}
					await locker.LockAsync();
					_state.VersionSet.LastSequence = lastSequence;

					foreach (var outstandingWrite in list)
					{
						// notify items we already worked on...
						outstandingWrite.Result.SetResult(null);
					}
				}
				finally
				{
					_writeCompletedEvent.Pulse();	
				}
			}
		}

		private async Task MakeRoomForWrite(bool force, AsyncLock.LockScope lockScope)
		{
			bool allowDelay = force == false;
			while (true)
			{
				if (_state.BackgroundTask.IsCanceled || _state.BackgroundTask.IsFaulted)
				{
					await _state.BackgroundTask;// throws
				}
				else if (allowDelay && _state.VersionSet.GetNumberOfFilesAtLevel(0) >= Config.SlowdownWritesTrigger)
				{
					// We are getting close to hitting a hard limit on the number of
					// L0 files.  Rather than delaying a single write by several
					// seconds when we hit the hard limit, start delaying each
					// individual write by 1ms to reduce latency variance.  Also,
					// this delay hands over some CPU to the compaction thread in
					// case it is sharing the same core as the writer.
					lockScope.Exit();
					{
						await Task.Delay(TimeSpan.FromMilliseconds(1));
					}
					await lockScope.LockAsync();
					allowDelay = false; // Do not delay a single write more than once
				}
				else if (force == false && _state.MemTable.ApproximateMemoryUsage <= _state.Options.WriteBatchSize)
				{
					// There is room in current memtable
					break;
				}
				else if (_state.ImmutableMemTable != null)
				{
					// We have filled up the current memtable, but the previous
					// one is still being compacted, so we wait.
					await _state.BackgroundTask;
				}
				else if (_state.VersionSet.GetNumberOfFilesAtLevel(0) >= Config.StopWritesTrigger)
				{
					// There are too many level-0 files.
					await _state.BackgroundTask;
				}
				else
				{
					// Attempt to switch to a new memtable and trigger compaction of old
					Debug.Assert(_state.VersionSet.PrevLogNumber == 0);

					_state.LogWriter.Dispose();

					_state.CreateNewLog();
					_state.ImmutableMemTable = _state.MemTable;
					_state.MemTable = new MemTable(_state.Options);
					force = false;
					MaybeScheduleCompaction(lockScope);
				}
			}
		}

		private void MaybeScheduleCompaction(AsyncLock.LockScope lockScope)
		{
			Debug.Assert(lockScope != null);
			if (_state.BackgroundCompactionScheduled)
			{
				return; // alread scheduled, nothing to do
			}
			if (_state.ShuttingDown)
			{
				return;    // DB is being disposed; no more background compactions
			}
			if (_state.ImmutableMemTable == null &&
				// _state.manual_compaction_ == null &&
				_state.VersionSet.NeedsCompaction)
			{
				// No work to be done
				return;
			}
			_state.BackgroundCompactionScheduled = true;
			_state.BackgroundTask = Task.Factory.StartNew(RunCompaction);
		}

		private async void RunCompaction()
		{
			var status = this.BackgroundCompaction();
			if (status.IsOK())
			{
				// Success
			}
			else
			{
				// Wait a little bit before retrying background compaction in
				// case this is an environmental problem and we do not want to
				// chew up resources for failed compactions for the duration of
				// the problem.

				Thread.Sleep(1000000);
			}

			using (var locker = await _state.Lock.LockAsync())
			{
				this.MaybeScheduleCompaction(locker);
			}
		}

		private Status BackgroundCompaction()
		{
			if (_state.ImmutableMemTable != null)
			{
				return CompactMemTable();
			}

			return Status.OK();
		}

		/// <summary>
		/// Compact the in-memory write buffer to disk.  Switches to a new
		/// log-file/memtable and writes a new descriptor if successful.
		/// </summary>
		private Status CompactMemTable()
		{
			if (_state.ImmutableMemTable == null)
				throw new InvalidOperationException("ImmutableMemTable cannot be null.");

			var immutableMemTable = _state.ImmutableMemTable;

			VersionEdit edit = new VersionEdit();
			Version currentVersion = this._state.VersionSet.Current;

			Status status = WriteLevel0Table(immutableMemTable, edit, currentVersion);

			// Replace immutable memtable with the generated Table
			if (status.IsOK())
			{
				edit.SetPrevLogNumber(0);
				edit.SetLogNumber(_state.LogFileNumber);
				status = this._state.LogAndApply(edit); // maybe add mutex?
			}
			
			if (status.IsOK())
			{
				this._state.ImmutableMemTable = null;
				DeleteObsoleteFiles();
			}

			return status;
		}

		private void DeleteObsoleteFiles()
		{
			var live = pendingOutputs;
			_state.VersionSet.AddLiveFiles(live);

			var databaseName = _state.DatabaseName;
			var databaseFiles = new DirectoryInfo(databaseName).GetFiles();

			foreach (var file in databaseFiles)
			{
				ulong number;
				FileType fileType;
				if (_state.FileSystem.TryParseDatabaseFile(file, out number, out fileType))
				{
					var keep = true;
					switch (fileType)
					{
						case FileType.LogFile:
							keep = ((number >= _state.VersionSet.LogNumber) || (number == _state.VersionSet.PrevLogNumber));
							break;
						case FileType.DescriptorFile:
							// Keep my manifest file, and any newer incarnations'
							// (in case there is a race that allows other incarnations)
							keep = (number >= _state.VersionSet.ManifestFileNumber);
							break;
						case FileType.TableFile:
							keep = (live.IndexOf(number) != live.Count() - 1);
							break;
						case FileType.TempFile:
							// Any temp files that are currently being written to must
							// be recorded in pending_outputs_, which is inserted into "live"
							keep = (live.IndexOf(number) != live.Count() - 1);
							break;
						case FileType.CurrentFile:
						case FileType.DBLockFile:
						case FileType.InfoLogFile:
							break;
						default:
							throw new NotSupportedException(fileType.ToString());
					}

					if (!keep)
					{
						if (fileType == FileType.TableFile)
						{
							//table_cache_->Evict(number);
						}

						//Log(options_.info_log, "Delete type=%d #%lld\n",
						//int(type),
						//static_cast<unsigned long long>(number));

						File.Delete(string.Format("{0}/{1}", databaseName, file));
					}
				}
			}
		}

		private Status WriteLevel0Table(MemTable memTable, VersionEdit edit, Version currentVersion)
		{
			var stopwatch = Stopwatch.StartNew();
			var fileNumber = this._state.VersionSet.NewFileNumber();

			pendingOutputs.Add(fileNumber);

			var fileMetadata = _state.BuildTable(memTable, fileNumber);

			pendingOutputs.Remove(fileNumber);

			// Note that if file_size is zero, the file has been deleted and
			// should not be added to the manifest.
			int level = 0;
			if (fileMetadata.FileSize > 0)
			{
				var smallestKey = fileMetadata.SmallestKey;
				var largestKey = fileMetadata.LargestKey;

				if (currentVersion != null)
				{
					level = currentVersion.PickLevelForMemTableOutput(smallestKey, largestKey);
				}

				edit.AddFile(level, fileMetadata);
			}

			_state.CompactionStats[level].Add(new CompactionStats
				                                  {
					                                  Micros = stopwatch.ElapsedMilliseconds,
													  BytesRead = 0,
													  BytesWritten = fileMetadata.FileSize
				                                  });

			return Status.OK();
		}

		private List<OutstandingWrite> BuildBatchGroup(OutstandingWrite mine)
		{
			// Allow the group to grow up to a maximum size, but if the
			// original write is small, limit the growth so we do not slow
			// down the small write too much.
			long maxSize = 1024 * 1024; // 1 MB by default
			if (mine.Size < 128 * 1024)
				maxSize = mine.Size + (128 * 1024);

			var list = new List<OutstandingWrite> { mine };
			foreach (var item in _pendingWrites.Skip(1)) //skip the first since we already added it
			{
				maxSize -= item.Size;
				if (maxSize < 0)
					break;
				list.Add(item);
			}
			return list;
		}
	}
}