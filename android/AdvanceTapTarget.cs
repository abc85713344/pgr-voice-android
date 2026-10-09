namespace PgrVoice.AndroidApp;

/// <summary>Coordinates come from the actual display overlay, never from a resized app capture.</summary>
public sealed record AdvanceTapTarget(string GamePackage,int DisplayWidth,int DisplayHeight,int Rotation,
    float Left,float Top,float Width,float Height)
{
    // GamePackage 仅用于兼容读取旧设置，区域按物理屏幕保存，不绑定客户端。
    public bool IsValid => DisplayWidth>0 && DisplayHeight>0 && Rotation is >=0 and <=3 &&
        float.IsFinite(Left)&&float.IsFinite(Top)&&float.IsFinite(Width)&&float.IsFinite(Height)&&
        Left>=0&&Top>=0&&Width>0&&Height>0&&Left+Width<=1.001f&&Top+Height<=1.001f;
    public float CenterX=>(Left+Width/2)*DisplayWidth;
    public float CenterY=>(Top+Height/2)*DisplayHeight;
}
