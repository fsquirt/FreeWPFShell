using System;
using System.IO;
using System.Threading;

namespace FreeWPFShell.Share
{
    public sealed class PausableStream : Stream
    {
        private readonly Stream _inner;
        private readonly ManualResetEventSlim _gate;
        private readonly CancellationToken _token;

        public PausableStream(Stream inner, ManualResetEventSlim gate, CancellationToken token)
        {
            _inner = inner;
            _gate = gate;
            _token = token;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count)
        {
            WaitIfPaused();
            return _inner.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WaitIfPaused();
            _inner.Write(buffer, offset, count);
        }

        private void WaitIfPaused()
        {
            _token.ThrowIfCancellationRequested();
            if (_gate.IsSet) return;
            _gate.Wait(_token);
            _token.ThrowIfCancellationRequested();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
