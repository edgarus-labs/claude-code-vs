using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace ClaudeCode.Core.ViewModels;

/// <summary>
/// Represents a view model for displaying permission request details including title, options, and a command to choose a permission.
/// </summary>
public sealed class PermissionRequestViewModel
{
    /// <summary>
    /// Initializes a new instance of the PermissionRequestViewModel class with the specified title, permission options, and selection callback, validating the callback parameter.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <param name="options">The collection of options.</param>
    /// <param name="choose">The choose.</param>
    /// <exception cref="ArgumentNullException">Thrown when an error occurs during execution.</exception>
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
