﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;
using Raven.Storage.Building;
using Raven.Storage.Data;
using Raven.Storage.Filtering;
using Raven.Storage.Impl;
using Raven.Storage.Memory;
using Raven.Storage.Reading;
using Xunit;

namespace Raven.Storage.Tests.SST
{
	public class ReadWrite : IDisposable
	{
		readonly List<FileStream> shouldHaveBeenDisposed = new List<FileStream>();

		public ReadWrite()
		{
			if (Directory.Exists("none") == false)
				Directory.CreateDirectory("none");
		}

		[Fact]
		public void CanReadValuesBackWithoutFilter()
		{
			var state = new StorageState("none", new StorageOptions
			{
				ParanoidChecks = true,
				FilterPolicy = null
			});
			string name;
			using (var file = CreateFile())
			{
				name = file.Name;
				using (var tblBuilder = new TableBuilder(state, file, new TemporaryFiles(state.FileSystem, 1)))
				{
					for (int i = 0; i < 10; i++)
					{
						string k = "tests/" + i.ToString("0000");
						tblBuilder.Add(new InternalKey(k, 1, ItemType.Value).TheInternalKey, new MemoryStream(Encoding.UTF8.GetBytes(k)));
					}

					tblBuilder.Finish();
					file.Flush(true);
				}
			}

			using (var mmf = MemoryMappedFile.CreateFromFile(name, FileMode.Open))
			{
				var length = new FileInfo(name).Length;
				using (var table = new Table(state, new FileData(new MemoryMappedFileAccessor(name, mmf), length)))
				using (var iterator = table.CreateIterator(new ReadOptions()))
				{
					for (int i = 0; i < 10; i++)
					{
						string k = "tests/" + i.ToString("0000");
						iterator.Seek(new InternalKey(k, 100, ItemType.Value).TheInternalKey);
						Assert.True(iterator.IsValid);
						using (var stream = iterator.CreateValueStream())
						using (var reader = new StreamReader(stream))
						{
							Assert.Equal(k, reader.ReadToEnd());
						}
					}
				}
			}
		}

		[Fact]
		public void CanReadValuesBack()
		{
			var state = new StorageState("none", new StorageOptions
				{
					ParanoidChecks = true,
					FilterPolicy = new BloomFilterPolicy()
				});
			const int count = 5;
			string name;
			using (var file = CreateFile())
			{
				name = file.Name;
				using (var tblBuilder = new TableBuilder(state, file, new TemporaryFiles(state.FileSystem, 1)))
				{
					for (int i = 0; i < count; i++)
					{
						string k = "tests/" + i.ToString("0000");
						tblBuilder.Add(new InternalKey(k, 1, ItemType.Value).TheInternalKey, new MemoryStream(Encoding.UTF8.GetBytes("values/" + i)));
					}

					tblBuilder.Finish();
					file.Flush(true);
				}
			}

			using (var mmf = MemoryMappedFile.CreateFromFile(name, FileMode.Open))
			{
				var length = new FileInfo(name).Length;
				using (var table = new Table(state, new FileData(new MemoryMappedFileAccessor(name, mmf), length)))
				using (var iterator = table.CreateIterator(new ReadOptions()))
				{
					for (int i = 0; i < count; i++)
					{
						string k = "tests/" + i.ToString("0000");
						iterator.Seek(new InternalKey(k, 1000, ItemType.Value).TheInternalKey);
						Assert.True(iterator.IsValid);
						using (var stream = iterator.CreateValueStream())
						using (var reader = new StreamReader(stream))
						{
							Assert.Equal("values/" + i, reader.ReadToEnd());
						}
					}
				}
			}
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		private FileStream CreateFile()
		{
			var stackTrace = new StackTrace();
			var f = File.Create(stackTrace.GetFrame(1).GetMethod().Name + ".rsst");

			shouldHaveBeenDisposed.Add(f);

			return f;
		}

		public void Dispose()
		{
			foreach (var stream in shouldHaveBeenDisposed)
			{
				File.Delete(stream.Name);
			}
			Directory.Delete("none", true);
		}
	}
}