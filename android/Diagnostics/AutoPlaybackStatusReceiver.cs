#if DEBUG
using Android.App;
using Android.Content;
using System.Text.Json;
namespace PgrVoice.AndroidApp.Diagnostics;
[BroadcastReceiver(Name="cn.pgrvoice.player.AutoPlaybackStatusReceiver",Exported=true)]
public sealed class AutoPlaybackStatusReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context,Intent? intent)
    {
        if(context==null)return;
        var session=AppSession.Get(context);
        File.WriteAllText(Path.Combine(context.FilesDir!.AbsolutePath,"autoplay-status.json"),JsonSerializer.Serialize(new{
            session.Status,session.OcrText,session.AutoPlaybackRunning,session.AutoPlaybackPhaseName,
            node=session.Engine?.CurrentId,mode=session.Engine?.Mode.ToString(),
            diagnostics=JsonDocument.Parse(session.Diagnostics.Export()).RootElement.Clone()
        },Json.Options));
    }
}
#endif
