﻿using System;
using System.IO;

namespace Raven.Storage.Impl
{
	public class FileSystem : IDisposable
	{
		public virtual Stream NewWritable(string name)
		{
			return File.OpenWrite(name);
		}

		public Stream NewWritable(string name, ulong num, string ext)
		{
			return NewWritable(string.Format("{0}{1:000000}.{2}", name, num, ext));
		}

		public void Dispose()
		{
			
		}
	}
}