using Android.Views;
using Android.Widget;
using PgrVoice.AndroidApp.Ui;

namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    void BranchSettingsPage()
    {
        Line("游戏配音",11); Line("分支线设置",24);
        Line("选择遇到分支时怎样继续。设置会保存，下次仍使用你的选择。",13);
        DefaultBranchPlaybackSettings();
        if (!session.Settings.AutoPlayConfirmBranch)
        {
        Line("常规分支处理",17);
        Line("用于点按跟随，以及未开启默认分支的自动播放。",13);
        var modes=new RadioGroup(this){Orientation=Orientation.Vertical};
        var follow=new RadioButton(this){Id=View.GenerateViewId(),Text="分支配音跟随",TextSize=16,Checked=session.Settings.BranchAutoFollow};
        var pause=new RadioButton(this){Id=View.GenerateViewId(),Text="分支期间暂停配音",TextSize=16,Checked=!session.Settings.BranchAutoFollow};
        foreach(var option in new[]{follow,pause})
        {
            option.SetTextColor(PgrTheme.Foreground);option.SetMinimumHeight(Dp(52));
            option.SetPadding(Dp(10),Dp(6),Dp(10),Dp(6));modes.AddView(option);
        }
        modes.Check(session.Settings.BranchAutoFollow?follow.Id:pause.Id);
        modes.CheckedChange+=(_,e)=>
        {
            if(e.CheckedId!=follow.Id&&e.CheckedId!=pause.Id)return;
            bool enabled=e.CheckedId==follow.Id;
            if(session.Settings.BranchAutoFollow==enabled)return;
            session.SetBranchAutoFollow(enabled);Render();
        };
        content.AddView(modes,new LinearLayout.LayoutParams(-1,-2));
        var explanation=Card(content);
        if(session.Settings.BranchAutoFollow)
        {
            explanation.AddView(Text("未开启默认分支自动播放时，自动播放到分支会先暂停，并在顶部提示：已到分支，请先在游戏中选择，再确认并开启点按跟随。",14));
            explanation.AddView(Text("1. 先在游戏里选择分支，等当前台词出现。",14));
            explanation.AddView(Text("2. 展开顶部通知，选与游戏相同的台词并确认；也可按选项核对同名分支。确认会播放该句，并使用已保存区域开启点按跟随，不用再去控制页开启一次。长句可点“全文”核对。",14));
            explanation.AddView(Text("3. 支线期间不会自动点下一句。等字幕完整后，在保存的下一句区域轻点一次，游戏和配音一起推进。",14));
            explanation.AddView(Text("4. 原来用自动播放的，回到已核实共同线时会在顶部提示具体台词；游戏出现这句后确认恢复自动播放。原来用点按跟随的不会改成自动播放，遇到续接提示时按游戏画面核对。",14));
            explanation.AddView(Text("人物或话题也先在游戏中选好，再确认同项；聊完返回菜单后会再次通知。未知连接、屏幕变化或点击权限不可用时，会说明原因，需核对后继续。",13));
        }
        else explanation.AddView(Text("遇到普通分支后暂停配音和自动点击，由你自己玩分支。有明确汇合点时，顶部通知会预览共同线台词；游戏出现这句后，展开并确认继续。无需在软件中选择走了哪条分支。",14));
        }
        Line("需要确认时的顶部通知",17);
        Line("顶部通知先显示简短内容，点开后查看和操作；收起只缩小提示，保留等待，取消才退出本次跟随。请先对照游戏，确认一致后才继续；未核实汇合点或3D人物互动不会猜测共同线位置，可从台词列表或定位页重新对齐。",13);
        Line("下一句点击区域",17);
        Line("开始前在游戏里设置一次，自动播放与点按跟随共用。换屏幕或横竖屏后，使用对应屏幕保存的区域。",13);
        Button(content,"回到游戏设置下一句区域",()=>ReturnToGameForAutoPlayback(true));
        Button(content,"辅助点击权限",ExplainAutoPlaybackAccessibility);
        Line("这里的设置仅用于游戏配音，不改变听书的分支选择方式。",12);
    }

    void DefaultBranchPlaybackSettings()
    {
        Line("自动播放遇到选择时",17);
        var confirmBranch=new Switch(this)
        {
            Text="确认选后的对白，再继续播放",TextSize=16,
            Checked=session.Settings.AutoPlayConfirmBranch
        };
        confirmBranch.SetTextColor(PgrTheme.Foreground);confirmBranch.SetMinimumHeight(Dp(52));
        confirmBranch.SetPadding(Dp(12),Dp(8),Dp(12),Dp(8));
        confirmBranch.Background=PgrTheme.Surface(this,PgrTheme.Panel);
        confirmBranch.CheckedChange+=(_,e)=>{session.SetAutoPlayConfirmBranch(e.IsChecked);Render();};
        content.AddView(confirmBranch,new LinearLayout.LayoutParams(-1,-2));
        if(session.Settings.AutoPlayConfirmBranch)
            Line("如需设置默认第一或第二分支，请先关闭此开关，下方会显示对应设置。",13);
        if(session.Settings.AutoPlayConfirmBranch)
        {
            Line("平时沿当前路线播放，遇到选项才停。先在游戏里选择，再展开顶部通知，对照原选项和选后对白，点对应卡片继续。汇合时不需要再次确认。",13);
            Line("从你确认的实际对白开始，不补读被跳过的选项或台词。选项首句相同时，原选项文字仍保留；长句可点全文查看。软件不替你点击游戏选项，也不猜测你选了哪一项。",13);
            Line("选错时可在悬浮“分支与菜单”里重选最近一次选项，或从台词列表重新定位。尚未核实的连接、缺音和人物互动仍停下说明原因。",13);
            return;
        }
        Line("自动播放默认分支",17);
        var defaultBranch=new Switch(this)
        {
            Text="自动播放时使用默认分支",TextSize=16,
            Checked=session.Settings.AutoPlayDefaultBranchEnabled
        };
        defaultBranch.SetTextColor(PgrTheme.Foreground);defaultBranch.SetMinimumHeight(Dp(52));
        defaultBranch.SetPadding(Dp(12),Dp(8),Dp(12),Dp(8));
        defaultBranch.Background=PgrTheme.Surface(this,PgrTheme.Panel);
        defaultBranch.CheckedChange+=(_,e)=>
        {
            if(session.Settings.AutoPlayDefaultBranchEnabled==e.IsChecked)return;
            session.SetAutoPlayDefaultBranchEnabled(e.IsChecked);Render();
        };
        content.AddView(defaultBranch,new LinearLayout.LayoutParams(-1,-2));
        Line("开启后，自动播放按设定的路线续播。请在游戏选择对应选项，软件不会替你点击游戏选项。关闭后恢复下方常规处理方式，所选偏好仍会保留。",13);
        Line("到分支时，软件直接按默认项配音，不再等待识别或要求手动续接。请你在游戏中点同一项；顶部显示“进入分支一／二：选项内容”，约1.5秒后消失。选错时可暂停并重新定位。",13);
        Line("悬浮台词始终标明当前分支，关闭默认分支也保留标识。内层选项汇合后仍显示父分支。开启默认分支时，已确认的支线可在暂停后核对当前句重新开启；当前路线保持，默认项只用于下一处选择。",13);
        var defaults=new RadioGroup(this){Orientation=Orientation.Vertical};
        var first=new RadioButton(this){Id=View.GenerateViewId(),Text="分支一 · 第一个选项",TextSize=16};
        var second=new RadioButton(this){Id=View.GenerateViewId(),Text="分支二 · 第二个选项",TextSize=16};
        foreach(var option in new[]{first,second})
        {
            option.SetTextColor(PgrTheme.Foreground);option.SetMinimumHeight(Dp(52));
            option.SetPadding(Dp(10),Dp(6),Dp(10),Dp(6));defaults.AddView(option);
        }
        defaults.Check(session.Settings.DefaultBranchOption==2?second.Id:first.Id);
        defaults.CheckedChange+=(_,e)=>
        {
            if(e.CheckedId!=first.Id&&e.CheckedId!=second.Id)return;
            int selected=e.CheckedId==second.Id?2:1;
            if(session.Settings.DefaultBranchOption==selected)return;
            session.SetDefaultBranchOption(selected);Render();
        };
        content.AddView(defaults,new LinearLayout.LayoutParams(-1,-2));
        Line("此开关独立于下方设置，只联动游戏配音的自动播放；点按跟随和听书保持各自设置。人物互动、条件未明、缺少对应选项或尚未核实的路线仍会提示确认，不会换选另一条路线。",12);
    }
}
