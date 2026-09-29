namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    // 只保存用户可见的最后一次 OCR 证据；弱引用避免切章后因这条记录留住旧章节。
    sealed record OcrReviewEvidence(WeakReference<PlaybackEngine> Owner, string PackId, string SectionId,
        string DisplayText, IReadOnlyList<OcrBlock> Blocks);
    OcrReviewEvidence? lastOcrReviewEvidence;

    /// <summary>在接纳本次 OCR、赋值 OcrText 后调用。屏幕授权/旋转/跟随代次不会改写这份人工复核证据。</summary>
    void RememberOcrEvidence(PlaybackEngine engine, string section, IReadOnlyList<OcrBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(engine); ArgumentNullException.ThrowIfNull(blocks);
        if (!ReferenceEquals(Engine, engine)) return;
        if (!engine.Pack.Chapters.SelectMany(c => c.Sections).Any(s => s.Id == section))
        { lastOcrReviewEvidence = null; return; }
        var copied = blocks.Where(b => b != null).Select(b => new OcrBlock
        {
            Text = b.Text ?? "", Score = b.Score,
            Box = b.Box?.Where(p => p != null).Select(p => (double[])p.Clone()).ToArray() ?? Array.Empty<double[]>()
        }).ToArray();
        lastOcrReviewEvidence = new(new(engine), engine.Pack.Id, section, OcrText, copied);
    }

    bool IsCurrentOcrReviewEvidence(OcrReviewEvidence? evidence, PlaybackEngine? engine) => evidence != null && engine != null &&
        evidence.Owner.TryGetTarget(out var owner) && ReferenceEquals(engine, owner) &&
        engine.Pack.Id == evidence.PackId && OcrText == evidence.DisplayText &&
        engine.Pack.Chapters.SelectMany(c => c.Sections).Any(s => s.Id == evidence.SectionId);

    /// <summary>表示上次原文仍有同章识别证据；不表示已经找到匹配，更不表示可以自动播放。</summary>
    public bool CanReviewRecognizedText => !IsImporting && IsCurrentOcrReviewEvidence(lastOcrReviewEvidence, Engine);

    /// <summary>
    /// 只供用户明确点击“采用上次识别原文”时调用。重新生成候选，保持剧情位置和声音静止；
    /// true 仅表示有候选可供下一步确认，调用方不得据此 ConfirmCurrent 或自动采用第一条。
    /// </summary>
    public bool PrepareRecognizedTextCandidates()
    {
        var engine = Engine; var evidence = lastOcrReviewEvidence;
        if (Listening.IsPlaying) Listening.Pause();
        Invalidate(); audio.Stop(); engine?.PauseForBrowse();
        if (IsImporting || !ReferenceEquals(Engine, engine) || !ReferenceEquals(evidence, lastOcrReviewEvidence) ||
            !IsCurrentOcrReviewEvidence(evidence, engine))
        {
            Candidates = Array.Empty<MatchCandidate>();
            Status = IsImporting ? "章节正在导入，请完成后再采用识别原文。" :
                engine == null ? "请先导入并打开章节，再识别或手动选择台词。" :
                evidence == null ? "还没有可采用的识别证据，请重新 OCR 定位，或在台词页手动选句。" :
                "上次识别原文不属于当前打开的章节，或原文证据已变化。请重新识别，不能采用旧结果。";
            ContentChanged?.Invoke(); Notify(); return false;
        }

        Candidates = Matcher.Find(engine!, evidence!.SectionId, evidence.Blocks.ToList())
            .Where(c => c.Node.Kind is "line" or "choice" && !c.Node.Archived &&
                engine!.Pack.ById.TryGetValue(c.Node.Id, out var node) && ReferenceEquals(node, c.Node)).ToArray();
        bool found = Candidates.Count > 0;
        Status = found ? $"已从上次识别原文找到 {Candidates.Count} 条候选。请选择与游戏一致的台词，再确认采用。" :
            "上次识别原文没有匹配到本章台词，无法采用。请重新识别或手动选句；当前配音位置没有改变。";
        ContentChanged?.Invoke(); Notify();
        return found;
    }
}
