namespace Bemo

// Installed before any UI is created in main; retained as a compatibility plugin.
type ExceptionHandlerPlugin() =
    interface IPlugin with
        member x.init() = ()
