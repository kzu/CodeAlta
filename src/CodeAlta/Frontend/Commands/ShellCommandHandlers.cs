using CodeAlta.App;
using CodeAlta.Models;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Frontend.Commands;

internal static class PromptCommandHandlers
{
    public static void Register(ShellCommandRegistry registry, ThreadCommandCoordinator threadCommands)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(threadCommands);

        registry.Register<SubmitPromptCommand>((command, cancellationToken) => ToValueTask(threadCommands.SendPromptAsync(command.Text, command.Steer, cancellationToken)));
    }

    private static ValueTask ToValueTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new ValueTask(task);
    }
}

internal static class ThreadCommandHandlers
{
    public static void Register(
        ShellCommandRegistry registry,
        ThreadCommandCoordinator threadCommands)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(threadCommands);

        registry.Register<AbortSelectedThreadCommand>((_, _) => ToValueTask(threadCommands.AbortSelectedThreadAsync()));
        registry.Register<CompactSelectedThreadCommand>((_, _) => ToValueTask(threadCommands.CompactSelectedThreadAsync()));
        registry.Register<ClearSelectedThreadQueueCommand>((_, _) => ToValueTask(threadCommands.ClearSelectedThreadQueueAsync()));
    }

    private static ValueTask ToValueTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new ValueTask(task);
    }
}

internal static class NavigationCommandHandlers
{
    public static void Register(
        ShellCommandRegistry registry,
        IShellNavigationCommandService navigationCommandService)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(navigationCommandService);

        registry.Register<FocusSidebarCommand>((_, _) =>
        {
            navigationCommandService.FocusSidebar();
            return ValueTask.CompletedTask;
        });
        registry.Register<FocusPromptCommand>((_, _) =>
        {
            navigationCommandService.FocusPrompt();
            return ValueTask.CompletedTask;
        });
        registry.Register<FocusModelProviderCommand>((_, _) =>
        {
            navigationCommandService.FocusModelProvider();
            return ValueTask.CompletedTask;
        });
        registry.Register<SelectRelativeTabCommand>((command, _) => ToValueTask(navigationCommandService.SelectRelativeTabAsync(command.Offset)));
        registry.Register<ScrollSelectedThreadMessageCommand>((command, _) => ToValueTask(navigationCommandService.ScrollSelectedThreadMessageAsync(command.Target)));
    }

    private static ValueTask ToValueTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new ValueTask(task);
    }
}

internal static class DialogCommandHandlers
{
    public static void Register(
        ShellCommandRegistry registry,
        Func<string?, Task> showHelpAsync,
        Action showCommandPalette,
        Action<string?> showOpenFolderDialog,
        IShellDialogCommandService dialogCommandService)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(showHelpAsync);
        ArgumentNullException.ThrowIfNull(showCommandPalette);
        ArgumentNullException.ThrowIfNull(showOpenFolderDialog);
        ArgumentNullException.ThrowIfNull(dialogCommandService);

        registry.Register<OpenHelpCommand>((command, _) => ToValueTask(showHelpAsync(command.FilterText)));
        registry.Register<OpenCommandPaletteCommand>((_, _) =>
        {
            showCommandPalette();
            return ValueTask.CompletedTask;
        });
        registry.Register<ExitAppCommand>((_, _) =>
        {
            dialogCommandService.ExitApp();
            return ValueTask.CompletedTask;
        });
        registry.Register<ToggleCommandBarMultiLineCommand>((_, _) =>
        {
            dialogCommandService.ToggleCommandBarMultiLine();
            return ValueTask.CompletedTask;
        });
        registry.Register<OpenFolderCommand>((command, _) =>
        {
            showOpenFolderDialog(command.InitialPath);
            return ValueTask.CompletedTask;
        });
        registry.Register<OpenAcpManagementCommand>((_, _) =>
        {
            dialogCommandService.OpenAcpManagement();
            return ValueTask.CompletedTask;
        });
        registry.Register<OpenAboutCommand>((_, _) =>
        {
            dialogCommandService.OpenAbout();
            return ValueTask.CompletedTask;
        });
        registry.Register<OpenModelProvidersCommand>((_, _) => ToValueTask(dialogCommandService.OpenModelProvidersAsync()));
        registry.Register<OpenModelsCommand>((_, _) =>
        {
            dialogCommandService.OpenModels();
            return ValueTask.CompletedTask;
        });
        registry.Register<OpenApplicationLogsCommand>((_, _) =>
        {
            dialogCommandService.OpenApplicationLogs();
            return ValueTask.CompletedTask;
        });
        registry.Register<OpenFileEditorCommand>((_, _) => ToValueTask(dialogCommandService.OpenFileEditorAsync()));
        registry.Register<OpenSkillsCommand>((_, _) => ToValueTask(dialogCommandService.OpenSkillsAsync()));
        registry.Register<OpenPluginsCommand>((_, _) => ToValueTask(dialogCommandService.OpenPluginsAsync()));
        registry.Register<OpenWorkspaceSettingsCommand>((_, _) =>
        {
            dialogCommandService.OpenWorkspaceSettings();
            return ValueTask.CompletedTask;
        });
        registry.Register<OpenSessionUsageCommand>((_, _) =>
        {
            dialogCommandService.OpenSessionUsage();
            return ValueTask.CompletedTask;
        });
        registry.Register<OpenThreadInfoCommand>((_, _) =>
        {
            dialogCommandService.OpenThreadInfo();
            return ValueTask.CompletedTask;
        });
        registry.Register<OpenExpandedPromptCommand>((_, _) =>
        {
            dialogCommandService.OpenExpandedPromptEditor();
            return ValueTask.CompletedTask;
        });
    }

    private static ValueTask ToValueTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new ValueTask(task);
    }
}

internal static class TabCommandHandlers
{
    public static void Register(ShellCommandRegistry registry, IShellTabCommandService tabCommandService)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(tabCommandService);

        registry.Register<CloseCurrentTabCommand>((_, _) => ToValueTask(tabCommandService.CloseCurrentTabAsync()));
    }

    private static ValueTask ToValueTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new ValueTask(task);
    }
}

internal static class PluginCommandHandlers
{
    public static void Register(
        ShellCommandRegistry registry,
        IPluginCommandService pluginCommandService,
        ThreadCommandCoordinator threadCommands,
        IShellStatusService statusService)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pluginCommandService);
        ArgumentNullException.ThrowIfNull(threadCommands);
        ArgumentNullException.ThrowIfNull(statusService);

        registry.Register<ExecutePluginTextCommand>((command, cancellationToken) => ToValueTask(ExecutePluginTextCommandAsync(
            pluginCommandService,
            threadCommands,
            statusService,
            command.CommandName,
            command.Arguments,
            cancellationToken)));
    }

    private static async Task ExecutePluginTextCommandAsync(
        IPluginCommandService pluginCommandService,
        ThreadCommandCoordinator threadCommands,
        IShellStatusService statusService,
        string name,
        string? arguments,
        CancellationToken cancellationToken)
    {
        var result = await pluginCommandService.ExecuteCommandAsync(name, arguments, cancellationToken);
        if (result.Disposition != PluginCommandDisposition.NotHandled)
        {
            if (!string.IsNullOrWhiteSpace(result.UserMessage))
            {
                statusService.SetStatus(result.UserMessage);
            }

            if (!string.IsNullOrWhiteSpace(result.PromptText))
            {
                await threadCommands.SendPromptAsync(result.PromptText, steer: false, cancellationToken);
            }

            return;
        }

        statusService.SetStatus(ShellCommandSurfaceCoordinator.BuildUnknownCommandStatus(name), tone: StatusTone.Warning);
    }

    private static ValueTask ToValueTask(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new ValueTask(task);
    }
}
