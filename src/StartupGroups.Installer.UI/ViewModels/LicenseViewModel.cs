using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StartupGroups.Installer.UI.ViewModels;

public sealed partial class LicenseViewModel : ObservableObject
{
    /// <summary>
    /// Embedded MIT text. Kept in sync with /LICENSE in the repo root by hand —
    /// short enough that drift is obvious. Phase 3c may move this to a resx file
    /// for translation.
    /// </summary>
    public string LicenseText { get; } = """
MIT License

Copyright (c) 2026 Startup Groups

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
""";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isAccepted;

    public event EventHandler? InstallRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? CancelRequested;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private void Install() => InstallRequested?.Invoke(this, EventArgs.Empty);
    private bool CanInstall() => IsAccepted;

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);
}
