﻿namespace Raven.Storage.Impl
{
	using System.Threading.Tasks;

	using Raven.Storage.Data;

	public interface IStorageCommands
	{
		Task CompactAsync(int level, Slice begin, Slice end);

		Task CompactRangeAsync(Slice begin, Slice end);

		Task CompactMemTableAsync();

		Task<StorageStatistics> GetStatisticsAsync();

		Task<Snapshot> CreateSnapshotAsync();

		Task ReleaseSnapshotAsync(Snapshot snapshot);
	}
}