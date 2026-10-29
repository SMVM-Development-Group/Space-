namespace SpacePlusPlus.Models;

public enum StorageCategory
{
    Applications,
    UserFiles,
    Media,
    Archives,
    Downloads,
    TempFiles,
    Development,
    CloudSync,
    Other
}

public static class CategoryInfo
{
    public static string Title(this StorageCategory c) => c switch
    {
        StorageCategory.Applications => "Applications & games",
        StorageCategory.UserFiles => "User files",
        StorageCategory.Media => "Media & images",
        StorageCategory.Archives => "Archives & disk images",
        StorageCategory.Downloads => "Downloads",
        StorageCategory.TempFiles => "Temporary files",
        StorageCategory.Development => "Development files",
        StorageCategory.CloudSync => "Cloud-synchronized files",
        _ => "Other files"
    };

    public static string ColorHex(this StorageCategory c) => c switch
    {
        StorageCategory.Applications => "#6C8CFF",
        StorageCategory.UserFiles => "#B08CF0",
        StorageCategory.Media => "#E38BC9",
        StorageCategory.Archives => "#D9A94E",
        StorageCategory.Downloads => "#53C7B4",
        StorageCategory.TempFiles => "#E5904C",
        StorageCategory.Development => "#97C554",
        StorageCategory.CloudSync => "#4FC3F7",
        _ => "#8A8D98"
    };

    public static string IconKey(this StorageCategory c) => c switch
    {
        StorageCategory.Applications => "apps",
        StorageCategory.UserFiles => "file",
        StorageCategory.Media => "media",
        StorageCategory.Archives => "archive",
        StorageCategory.Downloads => "download",
        StorageCategory.TempFiles => "trash",
        StorageCategory.Development => "code",
        StorageCategory.CloudSync => "cloud",
        _ => "folder"
    };
}
