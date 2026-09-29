#if DEBUG
using Android.App;
using Android.OS;
using System.Text.Json;

namespace PgrVoice.AndroidApp.Diagnostics;

/// <summary>调试构建的设备端回归入口。调用真实会话，不替代界面或真机验收。</summary>
[Activity(Name="cn.pgrvoice.player.SessionDiagnosticActivity",Exported=true,NoHistory=true)]
public sealed class SessionDiagnosticActivity : Activity
{
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string directory=FilesDir!.AbsolutePath;
        var results=new List<object>();
        try
        {
            var session=AppSession.Get(this);session.SetUiVisible(false);
            string scriptName=Path.GetFileName(Intent?.GetStringExtra("script")??"session-test.json");
            var commands=JsonSerializer.Deserialize<List<TestCommand>>(File.ReadAllText(Path.Combine(directory,scriptName)),Json.Options)??new();
            foreach(var command in commands)
            {
                switch(command.Action)
                {
                    case "import":
                        using(var input=File.OpenRead(Path.Combine(directory,Path.GetFileName(command.Value))))await session.ImportAsync(input,CancellationToken.None);
                        break;
                    case "load":session.LoadPack(command.Value);break;
                    case "line":session.ConfirmLine(command.Value,session.Engine?.Mode==RunMode.Original);break;
                    case "next":session.Command(e=>e.Next(true),true);break;
                    case "previous":session.Command(e=>e.Previous(),true);break;
                    case "confirm":session.ConfirmCurrent();break;
                    case "pause":session.Command(e=>e.PauseForBrowse());break;
                    case "original":session.Command(e=>e.EnterOriginal());break;
                    case "manual":session.SetMode(Following.FollowMode.Manual);break;
                    case "automatic":session.SetMode(Following.FollowMode.Automatic);break;
                    case "bookmark":
                        var e=session.Engine??throw new InvalidOperationException("没有章节。");session.Progress.SaveBookmark(e.Pack.Id,e.CreateBookmark(command.Value));break;
                    case "restoreBookmark":
                        session.Command(e=>{var marks=session.Progress.Bookmarks(e.Pack.Id);var mark=string.IsNullOrEmpty(command.Value)?marks.First():marks.First(m=>m.Label==command.Value||m.Id==command.Value);if(!e.RestoreBookmark(mark))throw new InvalidOperationException(e.NavigationError);});break;
                    case "mute":session.Settings.Volume=0;session.ApplyAudioSettings();break;
                    case "autoplayPackage":session.Settings.DebugAutoPlayPackage=command.Value;session.SaveSettings();break;
                    case "stop":session.Stop();break;
                    case "wait":break;
                    default:throw new InvalidDataException("未知测试命令："+command.Action);
                }
                if(command.WaitMs>0)await Task.Delay(Math.Min(command.WaitMs,10_000));
                var engine=session.Engine;
                bool passed=(command.ExpectNode==null||command.ExpectNode==engine?.CurrentId)&&
                    (command.ExpectMode==null||command.ExpectMode==engine?.Mode.ToString())&&
                    (command.ExpectArmed==null||command.ExpectArmed==session.IsArmed);
                results.Add(new{command.Action,passed,actualNode=engine?.CurrentId,actualMode=engine?.Mode.ToString(),armed=session.IsArmed,
                    history=engine?.History.Count,status=session.Status,bookmarkCount=engine==null?0:session.Progress.Bookmarks(engine.Pack.Id).Count});
                if(!passed)throw new InvalidOperationException("设备端回归断言失败："+command.Action);
            }
            File.WriteAllText(Path.Combine(directory,"session-report.json"),JsonSerializer.Serialize(new{passed=true,results},Json.Options));
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(directory,"session-report.json"),JsonSerializer.Serialize(new{passed=false,results,error=ex.ToString()},Json.Options));}
        finally{Finish();}
    }
    public sealed class TestCommand
    {
        public string Action{get;set;}="";
        public string Value{get;set;}="";
        public string? ExpectNode{get;set;}
        public string? ExpectMode{get;set;}
        public bool? ExpectArmed{get;set;}
        public int WaitMs{get;set;}=150;
    }
}
#endif
