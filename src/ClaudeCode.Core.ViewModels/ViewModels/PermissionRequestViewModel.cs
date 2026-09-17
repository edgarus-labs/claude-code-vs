using System;
using System.Collections.Generic;
using System.Windows.Input;
using ClaudeCode.Contracts;
using CommunityToolkit.Mvvm.Input;

namespace ClaudeCode.Core.ViewModels
{
    /// <summary>Backs the permission-request banner: one tool call awaiting Allow/Allow-always/Reject.</summary>
    public sealed class PermissionRequestViewModel
    {
        public PermissionRequestViewModel(string title, IReadOnlyList<PermissionOption> options, Action<PermissionOption> choose)
        {
            if (choose == null)
            {
                throw new ArgumentNullException(nameof(choose));
            }

            Title = title;
            Options = options;
            ChooseCommand = new RelayCommand<PermissionOption>(option =>
            {
                if (option != null)
                {
                    choose(option);
                }
            });
        }

        public string Title { get; }

        public IReadOnlyList<PermissionOption> Options { get; }

        /// <summary>Parameter is the chosen <see cref="PermissionOption"/>.</summary>
        public ICommand ChooseCommand { get; }
    }
}
