using System;
using System.Collections.Generic;
using System.IO;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The blob store of an editing session: new blobs go to one working store (a cache that can always be rebuilt), and reads also
    /// look in the stores the saved maps keep their blobs in. A map's folder holds its own blobs, so the session cannot write there
    /// before the user saves, and cannot read only from the cache either: the blobs of a map that was just opened live in its folder.
    ///
    /// The fallback stores are asked for on every read, so a map opened or saved after the session started is found too.
    /// Collecting unused blobs (<see cref="EnumerateHashes"/>, <see cref="TryDelete"/>) only ever touches the working store.
    /// </summary>
    public sealed class SC2LayeredBlobStore : ISC2BlobStore
    {
        private readonly ISC2BlobStore _working;
        private readonly Func<IEnumerable<ISC2BlobStore>> _fallbacks;

        public SC2LayeredBlobStore(ISC2BlobStore working, Func<IEnumerable<ISC2BlobStore>> fallbacks)
        {
            _working = working ?? throw new ArgumentNullException(nameof(working));
            _fallbacks = fallbacks;
        }

        private IEnumerable<ISC2BlobStore> Fallbacks() => _fallbacks?.Invoke() ?? Array.Empty<ISC2BlobStore>();

        public bool Contains(string hash)
        {
            if (_working.Contains(hash)) return true;
            foreach (var store in Fallbacks())
                if (store.Contains(hash)) return true;
            return false;
        }

        public Stream OpenRead(string hash)
        {
            if (_working.Contains(hash)) return _working.OpenRead(hash);
            foreach (var store in Fallbacks())
                if (store.Contains(hash)) return store.OpenRead(hash);
            // Nothing has it: the working store raises the "blob not found" error.
            return _working.OpenRead(hash);
        }

        public SC2BlobReference Put(Stream source) => _working.Put(source);

        public IEnumerable<string> EnumerateHashes() => _working.EnumerateHashes();

        public bool TryDelete(string hash) => _working.TryDelete(hash);
    }
}
