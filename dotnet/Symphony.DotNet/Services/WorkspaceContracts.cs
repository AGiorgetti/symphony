namespace Symphony.DotNet.Services;

internal enum WorkspaceHookKind
{
    AfterCreate,
    BeforeRun,
    AfterRun,
    BeforeRemove
}

internal enum HookFailureMode
{
    Propagate,
    Ignore
}
