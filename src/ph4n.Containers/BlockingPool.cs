﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ph4n.Common;

namespace ph4n.Containers
{
    public enum LoadingMode { Eager, Lazy, LazyExpanding };

    public enum AccessMode { FIFO, LIFO, Circular };


    public class BlockingPool<T> : IPool<T>, IDisposable
    {
        private bool isDisposed;

        private Func<T> factory;

        private LoadingMode loadingMode;

        private IItemStore<PooledItem> itemStore;

        private int size;

        private int count;

        private Semaphore sync;

        public BlockingPool(int size, Func<T> factory)
            : this(size, factory, LoadingMode.Lazy, AccessMode.FIFO)
        {
        }

        public BlockingPool(int size, Func<T> factory,
            LoadingMode loadingMode, AccessMode accessMode)
        {
            Validate.ArgumementGreaterThan(size, 0, "size");
            Validate.ArgumentNotNull(factory, "factory");

            this.size = size;
            this.factory = factory;
            sync = new Semaphore(size, size);
            this.loadingMode = loadingMode;
            this.itemStore = CreateItemStore(accessMode, size);
            if (loadingMode == LoadingMode.Eager)
            {
                PreloadItems();
            }
        }

        public IPooledItem<T> Acquire()
        {
            sync.WaitOne();
            switch (loadingMode)
            {
                case LoadingMode.Eager:
                    return AcquireEager();
                case LoadingMode.Lazy:
                    return AcquireLazy();
                default:
                    Debug.Assert(loadingMode == LoadingMode.LazyExpanding,
                        "Unknown LoadingMode encountered in Acquire method.");
                    return AcquireLazyExpanding();
            }
        }

        public void Release(IPooledItem<T> item)
        {
            lock (itemStore)
            {
                itemStore.Store((PooledItem)item);
            }
            sync.Release();
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }
            isDisposed = true;
            lock (itemStore)
            {
                while (itemStore.Count > 0)
                {
                    itemStore.Fetch().RemovedFromPool();
                }
            }
            sync.Close();
        }

        #region Acquisition

        private PooledItem AcquireEager()
        {
            lock (itemStore)
            {
                return itemStore.Fetch();
            }
        }

        private PooledItem AcquireLazy()
        {
            lock (itemStore)
            {
                if (itemStore.Count > 0)
                {
                    return itemStore.Fetch();
                }
            }
            Interlocked.Increment(ref count);
            return new PooledItem(this, factory());
        }

        private PooledItem AcquireLazyExpanding()
        {
            bool shouldExpand = false;
            if (count < size)
            {
                int newCount = Interlocked.Increment(ref count);
                if (newCount <= size)
                {
                    shouldExpand = true;
                }
                else
                {
                    // Another thread took the last spot - use the store instead
                    Interlocked.Decrement(ref count);
                }
            }
            if (shouldExpand)
            {
                return new PooledItem(this, factory());
            }
            else
            {
                lock (itemStore)
                {
                    return itemStore.Fetch();
                }
            }
        }

        private void PreloadItems()
        {
            for (int i = 0; i < size; i++)
            {
                var item = new PooledItem(this, factory());
                itemStore.Store(item);
            }
            count = size;
        }

        #endregion

        #region Collection Wrappers

        private IItemStore<PooledItem> CreateItemStore(AccessMode mode, int capacity)
        {
            switch (mode)
            {
                case AccessMode.FIFO:
                    return new QueueStore<PooledItem>(capacity);
                case AccessMode.LIFO:
                    return new StackStore<PooledItem>(capacity);
                default:
                    Debug.Assert(mode == AccessMode.Circular,
                        "Invalid AccessMode in CreateItemStore");
                    return new CircularStore<PooledItem>(capacity);
            }
        }

        #endregion

        public bool IsDisposed
        {
            get { return isDisposed; }
        }

        public class PooledItem : IPooledItem<T>
        {
            private T _internalItem;
            private IPool<T> _pool;

            public PooledItem(IPool<T> pool, T item)
            {
                Validate.ArgumentNotNull(pool, "pool");
                Validate.ArgumentNotNull(item, "item");

                this._pool = pool;
                this._internalItem = item;
            }

            public T Target { get { return _internalItem; } }

            internal void RemovedFromPool()
            {
                if (typeof(IDisposable).IsAssignableFrom(typeof(T)))
                {
                    ((IDisposable)_internalItem).Dispose();
                }
            }

            public void Dispose()
            {
                if (_pool.IsDisposed)
                {
                    RemovedFromPool();
                }
                else
                {
                    _pool.Release(this);
                }
            }
        }

        #region ItemStores

        interface IItemStore<TStore>
        {
            TStore Fetch();
            void Store(TStore item);
            int Count { get; }
        }

        internal class QueueStore<TStore> : Queue<TStore>, IItemStore<TStore>
        {
            public QueueStore(int capacity)
                : base(capacity)
            {
            }

            public TStore Fetch()
            {
                return Dequeue();
            }

            public void Store(TStore item)
            {
                Enqueue(item);
            }
        }

        internal class StackStore<TStore> : Stack<TStore>, IItemStore<TStore>
        {
            public StackStore(int capacity)
                : base(capacity)
            {
            }

            public TStore Fetch()
            {
                return Pop();
            }

            public void Store(TStore item)
            {
                Push(item);
            }
        }

        internal class CircularStore<TStore> : IItemStore<TStore>
        {
            private List<Slot> slots;
            private int freeSlotCount;
            private int position = -1;

            public CircularStore(int capacity)
            {
                slots = new List<Slot>(capacity);
            }

            public TStore Fetch()
            {
                if (Count == 0)
                    throw new InvalidOperationException("The buffer is empty.");

                int startPosition = position;
                do
                {
                    Advance();
                    Slot slot = slots[position];
                    if (!slot.IsInUse)
                    {
                        slot.IsInUse = true;
                        --freeSlotCount;
                        return slot.Item;
                    }
                } while (startPosition != position);
                throw new InvalidOperationException("No free slots.");
            }

            public void Store(TStore item)
            {
                Slot slot = slots.Find(s => object.Equals(s.Item, item));
                if (slot == null)
                {
                    slot = new Slot(item);
                    slots.Add(slot);
                }
                slot.IsInUse = false;
                ++freeSlotCount;
            }

            public int Count
            {
                get { return freeSlotCount; }
            }

            private void Advance()
            {
                position = (position + 1) % slots.Count;
            }

            class Slot
            {
                public Slot(TStore item)
                {
                    this.Item = item;
                }

                public TStore Item { get; private set; }
                public bool IsInUse { get; set; }
            }
        }

        #endregion ItemStores
    }


}
