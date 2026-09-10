using System.Collections.Generic;

namespace WordAI.Agent.Word
{
    /// <summary>文档基本信息。</summary>
    public sealed class DocumentInfo
    {
        public string FileName;
        public string FilePath;
        public int ParagraphCount;
        public int WordCount;
        public int CharacterCount;
        public bool Saved;
        public string ReadProtectionState; // "none" | "readOnly" | "writeReserved"
    }

    /// <summary>插入位置语义。</summary>
    public enum InsertPosition
    {
        /// <summary>插入到当前选区/光标处（Selection.TypeText 语义）。</summary>
        Selection,
        /// <summary>追加到文档末尾（Range.InsertAfter 语义）。</summary>
        DocumentEnd,
        /// <summary>插入到文档开头。</summary>
        DocumentStart
    }

    public sealed class InsertResult
    {
        /// <summary>实际插入的字符数。</summary>
        public int CharactersInserted;
        /// <summary>插入完成后的文档字符总数。</summary>
        public int TotalCharacters;
    }

    public sealed class SelectionInfo
    {
        public string Text;
        public int StartOffset;
        public int EndOffset;
        public string StyleName;
    }

    public sealed class FindHit
    {
        public int StartOffset;
        public int EndOffset;
        public string Context; // 命中前后各 32 字符的上下文片段
    }

    public sealed class FindResult
    {
        public List<FindHit> Hits = new List<FindHit>();
        public int TotalMatches;
    }

    public sealed class ReplaceResult
    {
        /// <summary>默认 false：仅统计并返回预览（高危操作，需 confirm 后执行）。</summary>
        public int ReplacementsMade;
        public int TotalMatches;
    }

    public sealed class StyleApplyResult
    {
        public int ParagraphsStyled;
        public string StyleName;
    }

    /// <summary>
    /// Word 宿主抽象：所有 word.* 网关方法的实际执行体。
    /// 由 VSTO 插件提供 COM 实现；测试中用 Fake 实现替代。
    /// 线程模型：所有调用都会被网关串行化投递，实现无需考虑并发。
    /// </summary>
    public interface IWordHost
    {
        DocumentInfo GetDocumentInfo();
        InsertResult InsertText(string text, InsertPosition position);
        SelectionInfo GetSelection();
        FindResult Find(string query, int maxHits, System.Threading.CancellationToken ct);
        ReplaceResult Replace(string find, string replace, bool all, System.Threading.CancellationToken ct);
        StyleApplyResult ApplyStyle(string paragraphStyle, bool apply, System.Threading.CancellationToken ct);
        void Save();
    }
}
