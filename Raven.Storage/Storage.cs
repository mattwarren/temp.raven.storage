﻿namespace Raven.Storage
{
	using System;

	using Raven.Storage.Impl;

	public class Storage : IDisposable
	{
		private readonly StorageState storageState;

		private bool wasDisposed = false;

		public string Name
		{
			get
			{
				return storageState.DatabaseName;
			}
		}

		public Storage(string name, StorageOptions options)
		{
			storageState = new StorageState(name, options);
			Init();
		}

		public Storage(StorageState storageState)
		{
			this.storageState = storageState;
			Init();
		}

		private void Init()
		{
			//TODO arek - add locking here
			var versionEdit = storageState.Recover();
			storageState.CreateNewLog();
			Writer = new StorageWriter(storageState);
			Reader = new StorageReader(storageState);
			Commands = new StorageCommands(storageState);
		}

		public IStorageCommands Commands { get; private set; }

		public StorageWriter Writer { get; private set; }

		public StorageReader Reader { get; private set; }

		public void Dispose()
		{
			if (wasDisposed)
				return;

			this.storageState.Dispose();
			wasDisposed = true;
		}
	}
}