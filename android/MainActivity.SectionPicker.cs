namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    void ChooseDisplayedSection(Pack pack, IEnumerable<Section> sections, string title,
        Func<bool> stillValid, Action<Section> selected)
    {
        var groups = SectionDisplay.Groups(sections);
        void Apply(Section section) { if (stillValid()) selected(section); }
        Choose(title, groups.Select(g => g.Title).ToArray(), index =>
        {
            if (!stillValid()) return;
            var group = groups[index];
            if (group.Sections.Count == 1) { Apply(group.Sections[0]); return; }
            Choose(group.Title, group.Sections.Select(s => SectionDisplay.SegmentLabel(pack, group, s)).ToArray(),
                part => Apply(group.Sections[part]));
        });
    }
}
