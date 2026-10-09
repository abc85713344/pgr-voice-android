using Android.App;
using Android.Views;
using Android.Widget;
using PgrVoice.Packages;

namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    LinearLayout chapterCategoryBar = null!;
    Button chapterCategoryButton = null!;

    void CreateChapterCategoryBar()
    {
        chapterCategoryBar = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.AddView(chapterCategoryBar, new LinearLayout.LayoutParams(-1, -2));
        chapterCategoryButton = Button(chapterCategoryBar, "选择分类", ChooseChapterCategory);
        ButtonIcon(chapterCategoryButton, "archive");
        chapterCategoryButton.SetSingleLine(true);
    }

    static string[] ChapterCategories(IReadOnlyList<InstalledPackage> packages) => ChapterCatalog.Categories
        .Concat(packages.Any(p => ChapterCatalog.Category(p.PackId, p.Title) == ChapterCatalog.Other)
            ? new[] { ChapterCatalog.Other } : Array.Empty<string>()).ToArray();

    string UpdateChapterCategory(IReadOnlyList<InstalledPackage> packages)
    {
        var categories = ChapterCategories(packages);
        chapterCategoryBar.Visibility = ViewStates.Visible;
        string selected = session.Settings.ChapterCategory ?? "";
        if (!categories.Contains(selected))
        {
            var current = session.Engine?.Pack;
            string currentCategory = current == null ? "" : ChapterCatalog.Category(current.Id, current.Title);
            selected = categories.Contains(currentCategory) ? currentCategory : categories.FirstOrDefault() ?? "";
            if (session.Settings.ChapterCategory != selected)
            {
                session.Settings.ChapterCategory = selected;
                session.SaveSettings();
                pageScroll.Post(() => { if (alive && page == 0) pageScroll.ScrollTo(0, 0); });
            }
        }
        chapterCategoryButton.Text = "分类：" + selected + "  ▾";
        chapterCategoryButton.ContentDescription = "选择章节分类，当前" + selected;
        return selected;
    }

    void ChooseChapterCategory()
    {
        var categories = ChapterCategories(session.Packages.List());
        AlertDialog? dialog = null;
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("选择分类");
        builder.SetSingleChoiceItems(categories, Array.IndexOf(categories, session.Settings.ChapterCategory), (_, e) => Safe(() =>
        {
            dialog?.Dismiss();
            // 分类只过滤目录；实际打开章节仍由章节卡的点击操作完成。
            if (session.Settings.ChapterCategory == categories[e.Which]) return;
            session.Settings.ChapterCategory = categories[e.Which];
            session.SaveSettings();
            Render();
            pageScroll.Post(() => { if (alive && page == 0) pageScroll.ScrollTo(0, 0); });
        }));
        builder.SetNegativeButton("取消", (_, _) => { });
        dialog = builder.Show();
        StyleDialog(dialog);
    }
}
