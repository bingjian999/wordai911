using System;
using System.Collections.Generic;
using System.Text;

namespace WordAI.Agent.Protocol
{
    /// <summary>
    /// JSON Lines 分帧解码器：累积字节流，按 \n 切出完整行。
    ///
    /// 在【字节】层面分帧后整行解码：\n (0x0A) 不会出现在 UTF-8 多字节序列内部，
    /// 因此按字节找换行再整体 GetString，可安全处理多字节字符被 TCP 分块切断的情况。
    /// 任何一行超过 maxLineBytes 仍未出现换行即判定超长（-32002），由调用方决定断开连接。
    /// </summary>
    public sealed class LineDecoder
    {
        private byte[] _buf = new byte[8192];
        private int _len;
        private readonly int _maxLineBytes;
        private readonly Action<string> _log;

        public LineDecoder(int maxLineBytes, Action<string> log = null)
        {
            if (maxLineBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxLineBytes));
            _maxLineBytes = maxLineBytes;
            _log = log ?? (_ => { });
        }

        /// <summary>当前累积缓冲长度（字节数）。</summary>
        public int BufferedLength => _len;

        /// <summary>
        /// 追加一段字节，切出所有完整行（不含 \n）。
        /// 若缓冲超过上限仍未出现换行，抛出 <see cref="LineTooLongException"/>。
        /// </summary>
        public IEnumerable<string> Append(byte[] data, int count)
        {
            if (count < 0 || (count > 0 && data == null) || count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            EnsureCapacity(_len + count);
            Array.Copy(data, 0, _buf, _len, count);
            _len += count;

            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < _len; i++)
            {
                if (_buf[i] == (byte)'\n')
                {
                    lines.Add(Encoding.UTF8.GetString(_buf, start, i - start));
                    start = i + 1;
                }
            }
            if (start > 0)
            {
                Array.Copy(_buf, start, _buf, 0, _len - start);
                _len -= start;
            }

            if (_len > _maxLineBytes)
            {
                _log($"[gateway] line exceeds {_maxLineBytes} bytes (buffered={_len}), dropping and closing");
                _len = 0;
                throw new LineTooLongException(_maxLineBytes);
            }
            return lines;
        }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _buf.Length) return;
            int cap = _buf.Length;
            while (cap < needed) cap *= 2;
            var grown = new byte[cap];
            Array.Copy(_buf, 0, grown, 0, _len);
            _buf = grown;
        }

        public sealed class LineTooLongException : Exception
        {
            public LineTooLongException(int limit) : base($"line exceeds {limit} bytes") { }
        }
    }
}
