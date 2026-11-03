using DbdPresence.Presentation.ViewModels;
using Microsoft.Win32;

namespace DbdPresence.App.Composition;

/// <summary>
/// The Windows file dialogs for the Packs page. The dialogs are the user's own choice of file; the app never reads a
/// path it did not get from here. UI tests that run with a redirected data root (<c>DBDP_DATA_ROOT</c>) can skip the dialog:
/// <c>DBDP_PACK_IMPORT_FILE</c> and <c>DBDP_PACK_IMAGE_FILE</c> then name the file to use. Without a redirected data root
/// both variables are ignored, so they cannot steer a real profile.
/// </summary>
internal sealed class WindowsFilePicker : IFilePicker
{
    private const string ImportVariable = "DBDP_PACK_IMPORT_FILE";
    private const string ImageVariable = "DBDP_PACK_IMAGE_FILE";

    public string? PickPackArchive() =>
        TestFile(ImportVariable) ?? Ask("Import a pack", "Pack archives (*.zip)|*.zip");

    public string? PickImage() =>
        TestFile(ImageVariable) ?? Ask("Choose an image", "Images (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp");

    private static string? TestFile(string variable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataRootVariable)))
        {
            return null;
        }

        var path = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? Ask(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            ValidateNames = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
