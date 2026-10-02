using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace ClaudeCode.Core.ViewModels;

public sealed class PermissionRequestViewModel
{
    public PermissionRequestViewModel(string title, IReadOnlyList<PermissionOption> options, Action<PermissionOption> choose)
    {
        if (choose is null)
        {
            throw new ArgumentNullException(nameof(choose));
        }

        Title = title;
        Options = options;
        ChooseCommand = new RelayCommand<PermissionOption>(option =>
        {
            if (option is not null)
            {
                choose(option);
            }
        });
    }

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the collection of options.
    /// </summary>
    public IReadOnlyList<PermissionOption> Options { get; }

    /// <summary>
    /// Gets the choose command.
    /// </summary>
    public ICommand ChooseCommand { get; }
}
