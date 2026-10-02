using Android.Content;
using Android.Provider;
using Uri = Android.Net.Uri;

namespace PgrVoice.AndroidApp.Platform;

/// <summary>只通过用户选择的 SAF 文档授权操作原 ZIP，不推算下载目录或文件路径。</summary>
public static class ImportedZipAccess
{
    public static ImportedZipSource Read(Context context, Uri uri)
    {
        using var cursor = context.ContentResolver!.Query(uri, null, null, null, null);
        if (cursor == null || !cursor.MoveToFirst()) throw new IOException("找不到原 ZIP，文件可能已移动或删除。");
        int name = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
        long? Number(string column)
        { int i = cursor.GetColumnIndex(column); return i < 0 || cursor.IsNull(i) ? null : cursor.GetLong(i); }
        return new(uri.ToString()!, name >= 0 ? cursor.GetString(name) ?? "章节 ZIP" : "章节 ZIP",
            Number(IOpenableColumns.Size), Number("last_modified"));
    }

    public static void PersistGrant(Context context, Uri uri, ActivityFlags flags)
    {
        var access = flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        if (access == 0 || !flags.HasFlag(ActivityFlags.GrantPersistableUriPermission)) return;
        try { context.ContentResolver!.TakePersistableUriPermission(uri, access); }
        catch (Java.Lang.SecurityException) { /* 仍可完成导入；原 ZIP 的后续操作会重新检查授权。 */ }
    }

    public static void Delete(Context context, ImportedZipSource expected)
    {
        using var uri = Uri.Parse(expected.Uri) ?? throw new IOException("原 ZIP 的文件地址无效。");
        if (!DocumentsContract.IsDocumentUri(context, uri))
            throw new IOException("这个文件来源不支持删除，请在系统文件管理中删除原 ZIP。");
        var current = Read(context, uri);
        if (current.Name != expected.Name || expected.Size is >= 0 && current.Size != expected.Size ||
            expected.Modified is > 0 && current.Modified != expected.Modified)
            throw new IOException("原 ZIP 自导入后已变化，为避免删除其他文件，请在系统文件管理中核对后删除。");
        using var cursor = context.ContentResolver!.Query(uri, new[] { "flags" }, null, null, null);
        // DocumentsContract.Document.FLAG_SUPPORTS_DELETE = 1 << 2。
        if (cursor == null || !cursor.MoveToFirst() || (cursor.GetInt(0) & 4) == 0)
            throw new IOException("文件来源没有提供删除权限，请在系统文件管理中删除原 ZIP。已导入的章节不受影响。");
        if (!DocumentsContract.DeleteDocument(context.ContentResolver, uri))
            throw new IOException("文件来源未确认删除成功，请在系统文件管理中检查原 ZIP。");
    }
}
