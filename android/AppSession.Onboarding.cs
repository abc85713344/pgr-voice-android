namespace PgrVoice.AndroidApp;

public sealed record OnboardingSelection(Pack Pack, Chapter Chapter, Node Node)
{
    public string? AudioPath => Pack.ResolveAudio(Node);
}

public sealed partial class AppSession
{
    public OnboardingStore Onboarding { get; private set; } = null!;
    public void PauseForOnboarding()
    {
        if(Listening.IsPlaying)Listening.Pause();
        Invalidate();audio.Stop();Engine?.PauseForBrowse();overlay.Hide();Notify();
    }
    public OnboardingSelection? SelectedOnboardingLine()
    {
        var state=Onboarding.Progress;
        if(state.PackId==null||state.ChapterId==null||state.NodeId==null)return null;
        var pack=Packages.Load(state.PackId);
        var chapter=pack.Chapters.FirstOrDefault(c=>c.Id==state.ChapterId);
        if(chapter==null||!pack.ById.TryGetValue(state.NodeId,out var node)||node.Kind!="line"||node.Archived||
            !chapter.Sections.Any(s=>s.Id==node.SectionId))return null;
        return new(pack,chapter,node);
    }
    public void OpenOnboardingChapter()
    {
        var state=Onboarding.Progress;
        if(state.Status!=OnboardingStatus.InProgress||state.Step!=OnboardingStep.Finish||!state.HeardConfirmed)
            throw new InvalidOperationException("请先完成单句试听并确认听到了声音。");
        var selected=SelectedOnboardingLine()??throw new InvalidOperationException("所选章节已变化，请重新选择。");
        if(selected.AudioPath is not { } audioPath||!File.Exists(audioPath))
            throw new InvalidOperationException("试听音频已经缺失，请重新选择或导入。");
        if(state.Purpose==OnboardingPurpose.Listening)Listening.Open(selected.Pack.Id,selected.Chapter.Id);
        else
        {
            LoadPack(selected.Pack.Id);
            // 只改变目录浏览到所选大章，不把试听台词提交为游戏当前位置。
            SectionId=selected.Chapter.Sections.First().Id;
        }
        state.Complete();Onboarding.Save();
    }
}
