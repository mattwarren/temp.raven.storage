﻿using System;
using System.Diagnostics;

namespace Raven.Storage.Impl
{
	public class VersionSet
	{
		private ulong _lastSequence;
		private Version _current = new Version();

		/// <summary>
		/// Return the last sequence number.
		/// </summary>
		public ulong LastSequence
		{
			get { return _lastSequence; }
			set
			{
				Debug.Assert(value >= _lastSequence);
				_lastSequence = value;
			}
		}

		/// <summary>
		/// Return the log file number for the log file that is currently
		/// being compacted, or zero if there is no such log file.
		/// </summary>
		public int PrevLogNumber { get; set; }

		public bool NeedsCompaction
		{
			get
			{
				var v = _current;
				return v.CompactionScore >= 1 || v.FileToCompact != null;
			}
		}

		public int GetNumberOfFilesAtLevel(int level)
		{
			return _current.Files[level].Count;
		}

		public int NewFileNumber()
		{
			throw new NotImplementedException();
		}

		public void ReuseFileNumber(int num)
		{
			throw new NotImplementedException();
		}
	}
}