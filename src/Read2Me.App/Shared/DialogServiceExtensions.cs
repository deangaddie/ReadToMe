using System.Threading.Tasks;
using MudBlazor;

namespace Read2Me.App.Shared
{
    public static class DialogServiceExtensions
    {
        /// <summary>
        /// Raises <see cref="ConfirmDialog"/> and returns true when the user confirmed. The title is
        /// MudBlazor's, so it is passed here rather than set on the dialog.
        /// </summary>
        public static async Task<bool> ConfirmAsync(
            this IDialogService dialogs, string title, string message, string confirmText)
        {
            var parameters = new DialogParameters<ConfirmDialog>
            {
                { d => d.Message, message },
                { d => d.ConfirmText, confirmText },
            };

            var dialog = await dialogs.ShowAsync<ConfirmDialog>(title, parameters);
            var result = await dialog.Result;
            return result?.Canceled == false;
        }
    }
}
