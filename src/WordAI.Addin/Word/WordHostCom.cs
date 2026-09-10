using System;
using System.Threading;
using WordAI.Agent.Word;
using Word = Microsoft.Office.Interop.Word;

namespace WordAI.Addin.Word
{
    /// <summary>
    /// IWordHost 的 COM 实现：通道 B 网关所有 word.* 方法的实际执行体。
    ///
    /// 线程模型：网关在后台线程调用本类；Word COM 对象只允许在 Word 主 STA
    /// 线程访问，因此每个调用经主线程 SynchronizationContext 同步 Send 过去
    /// 执行，调用方阻塞等待（网关侧已有 deadline / 超时 / 取消保护，
    /// 见 WordGatewayServer 注释——COM 调用按不可中断处理，执行完才返回）。
    ///
    /// 说明：本工程只能在 Windows + VS（VSTO 工作负载）下编译与联调，
    /// 沙箱/CI 不构建本工程。
    /// </summary>
    public sealed class WordHostCom : IWordHost
    {
        private readonly Func<Word.Application> _app;
        private readonly Func<Word.Document> _doc;
        private readonly SynchronizationContext _ui;

        public WordHostCom(Func<Word.Application> app, Func<Word.Document> doc, SynchronizationContext ui)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        }

        private Word.Application App => _app() ?? throw new InvalidOperationException("Word Application 尚未就绪");

        private Word.Document Doc => _doc() ?? throw new InvalidOperationException("当前没有打开的文档");

        private T OnMainThread<T>(Func<T> action) => _ui.Send(_ => action(), null);

        public DocumentInfo GetDocumentInfo()
        {
            return OnMainThread(() =>
            {
                var d = Doc;
                int paragraphs = 0, words = 0, characters = 0;
                try { paragraphs = d.Paragraphs.Count; } catch { }
                try { words = d.ComputeStatistics(Word.WdStatistic.wdStatisticWords, false); } catch { }
                try { characters = d.ComputeStatistics(Word.WdStatistic.wdStatisticCharactersWithSpaces, false); } catch { }

                string protection = "none";
                try
                {
                    if (d.ProtectionType != Word.WdProtectionType.wdNoProtection) protection = "protected";
                    else if (d.ReadOnly) protection = "readOnly";
                    else if (d.WriteReserved) protection = "writeReserved";
                }
                catch { }

                return new DocumentInfo
                {
                    FileName = d.Name ?? "",
                    FilePath = d.FullName ?? "",
                    ParagraphCount = paragraphs,
                    WordCount = words,
                    CharacterCount = characters,
                    Saved = d.Saved,
                    ReadProtectionState = protection,
                };
            });
        }

        public InsertResult InsertText(string text, InsertPosition position)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            return OnMainThread(() =>
            {
                var app = App;
                var d = Doc;
                switch (position)
                {
                    case InsertPosition.Selection:
                        app.Selection.TypeText(text); // 光标处逐字符录入（含撤销栈）
                        break;
                    case InsertPosition.DocumentEnd:
                        d.Content.InsertAfter(text);
                        break;
                    case InsertPosition.DocumentStart:
                        d.Content.InsertBefore(text);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(position));
                }
                return new InsertResult
                {
                    CharactersInserted = text.Length,
                    TotalCharacters = d.Content.Text == null ? 0 : d.Content.Text.Length,
                };
            });
        }

        public SelectionInfo GetSelection()
        {
            return OnMainThread(() =>
            {
                var s = App.Selection;
                string styleName = "";
                try { styleName = s.Style == null ? "" : s.Style.NameLocal; } catch { }
                return new SelectionInfo
                {
                    Text = s.Text ?? "",
                    StartOffset = s.Start,
                    EndOffset = s.End,
                    StyleName = styleName,
                };
            });
        }

        public FindResult Find(string query, int maxHits, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(query)) throw new ArgumentException("query 不能为空", nameof(query));
            if (maxHits <= 0) maxHits = 50;

            return OnMainThread(() =>
            {
                // 骨架实现：基于全文快照做顺序匹配（与 mock 网关语义一致）。
                // 正式版可换 Content.Find 以支持通配符/大小写选项。
                var text = Doc.Content.Text ?? "";
                var result = new FindResult();
                int idx = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int pos = text.IndexOf(query, idx, StringComparison.Ordinal);
                    if (pos < 0) break;
                    result.TotalMatches++;
                    if (result.Hits.Count < maxHits)
                    {
                        result.Hits.Add(new FindHit
                        {
                            StartOffset = pos,
                            EndOffset = pos + query.Length,
                            Context = SliceContext(text, pos),
                        });
                    }
                    idx = pos + query.Length;
                }
                return result;
            });
        }

        public ReplaceResult Replace(string find, string replace, bool all, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(find)) throw new ArgumentException("find 不能为空", nameof(find));

            return OnMainThread(() =>
            {
                var d = Doc;
                var text = d.Content.Text ?? "";
                int totalMatches = CountOccurrences(text, find);
                if (totalMatches == 0) return new ReplaceResult { ReplacementsMade = 0, TotalMatches = 0 };

                var rng = d.Content;
                rng.Find.ClearFormatting();
                rng.Find.Replacement.ClearFormatting();

                object oFind = find;
                object oReplace = replace ?? "";
                // 高危操作：网关层要求调用方（Pi 扩展）先经 ctx.ui.confirm 确认
                object oReplaceMode = all
                    ? (object)Word.WdReplace.wdReplaceAll
                    : (object)Word.WdReplace.wdReplaceOne;
                object m = Type.Missing;
                object forward = true;
                object wrap = (object)Word.WdFindWrap.wdFindStop;

                ct.ThrowIfCancellationRequested();
                rng.Find.Execute(
                    ref oFind, ref m, ref m, ref m, ref m, ref m,
                    ref forward, ref wrap, ref m, ref oReplace, ref oReplaceMode,
                    ref m, ref m, ref m, ref m);

                int remaining = CountOccurrences(d.Content.Text ?? "", find);
                return new ReplaceResult
                {
                    ReplacementsMade = totalMatches - remaining,
                    TotalMatches = totalMatches,
                };
            });
        }

        public StyleApplyResult ApplyStyle(string paragraphStyle, bool apply, CancellationToken ct)
        {
            return OnMainThread(() =>
            {
                var sel = App.Selection;
                object styleObj = (apply && !string.IsNullOrEmpty(paragraphStyle))
                    ? (object)paragraphStyle
                    : (object)"Normal"; // apply=false：还原为正文样式

                int styled = 0;
                foreach (Word.Paragraph p in sel.Paragraphs)
                {
                    ct.ThrowIfCancellationRequested();
                    p.set_Style(ref styleObj);
                    styled++;
                }
                return new StyleApplyResult
                {
                    ParagraphsStyled = styled,
                    StyleName = styleObj.ToString(),
                };
            });
        }

        public void Save()
        {
            OnMainThread<object>(() =>
            {
                var d = Doc;
                // 新文档（从未保存过）会弹出“另存为”对话框——由用户在 Word 侧完成
                d.Save();
                return null;
            });
        }

        /// <summary>命中点前后各 32 字符的上下文片段（回车显示为 \r 转义）。</summary>
        private static string SliceContext(string text, int pos, int radius = 32)
        {
            int start = Math.Max(0, pos - radius);
            int end = Math.Min(text.Length, pos + radius);
            return text.Substring(start, end - start)
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static int CountOccurrences(string text, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }
    }
}
