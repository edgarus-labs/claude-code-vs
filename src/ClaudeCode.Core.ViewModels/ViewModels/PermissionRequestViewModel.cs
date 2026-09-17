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

    public string Title { get; }

    public IReadOnlyList<PermissionOption> Options { get; }

    public ICommand ChooseCommand { get; }
}
