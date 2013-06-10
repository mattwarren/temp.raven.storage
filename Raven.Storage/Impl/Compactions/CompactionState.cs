﻿namespace Raven.Storage.Impl.Compactions
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;

	using Raven.Storage.Building;
	using Raven.Storage.Data;

	public class CompactionState : IDisposable
	{
		private readonly List<FileMetadata> outputs;

		public TableBuilder Builder { get; set; }

		public Compaction Compaction { get; private set; }

		public IReadOnlyList<FileMetadata> Outputs
		{
			get
			{
				return this.outputs.AsReadOnly();
			}
		}

		public FileMetadata CurrentOutput
		{
			get
			{
				return this.outputs.Last();
			}
		}

		public decimal SmallestSnapshot { get; set; }

		public Stream OutFile { get; set; }

		public long TotalBytes { get; set; }

		public CompactionState(Compaction compaction)
		{
			this.Compaction = compaction;
			this.SmallestSnapshot = -1;

			this.outputs = new List<FileMetadata>();
		}

		public void AddOutput(ulong fileNumber)
		{
			this.outputs.Add(new FileMetadata
			{
				FileNumber = fileNumber,
				SmallestKey = new InternalKey(),
				LargestKey = new InternalKey(),
				FileSize = 0
			});
		}

		public void Dispose()
		{
			if (this.Builder != null)
			{
				this.Builder.Dispose();
			}
			//else
			//{
			//	Debug.Assert(outFile == null);
			//}

			//if (outFile != null)
			//{
			//	outFile.Dispose();
			//}
		}
	}
}