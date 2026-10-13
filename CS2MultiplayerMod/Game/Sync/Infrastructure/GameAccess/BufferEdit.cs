using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Compares a buffer without dirtying its chunk; write access only on the first edit. Must not
    /// survive a structural change.
    /// </summary>
    internal struct BufferEdit<T> where T : unmanaged, IBufferElementData
    {
        private readonly EntityManager _entities;
        private readonly Entity _entity;
        private DynamicBuffer<T> _buffer;
        private bool _writable;

        public BufferEdit(EntityManager entities, Entity entity)
        {
            _entities = entities;
            _entity = entity;
            _buffer = entities.GetBuffer<T>(entity, true);
            _writable = false;
        }

        public int Length => _buffer.Length;

        public T this[int index]
        {
            get => _buffer[index];
            set { EnsureWritable(); _buffer[index] = value; }
        }

        public void Add(T value) { EnsureWritable(); _buffer.Add(value); }
        public void RemoveAt(int index) { EnsureWritable(); _buffer.RemoveAt(index); }
        public void Clear() { EnsureWritable(); _buffer.Clear(); }

        private void EnsureWritable()
        {
            if (_writable) return;
            _buffer = _entities.GetBuffer<T>(_entity);
            _writable = true;
        }
    }
}
