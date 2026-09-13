namespace Bemo
open System
open System.Drawing
open System.Reflection
open System.Collections.Generic

type ServiceAsyncResult() as this =
    let returnInvoker = InvokerService.invoker
    let mutable cachedResult = None
    let mutable cachedfCompleted = None
    let mutable completed = false

    member this.complete(result) =
        returnInvoker.asyncInvoke <| fun() ->
            cachedResult <- Some(result)
            this.tryToComplete()

    member this.tryToComplete() =
        if completed.not && cachedResult.IsSome && cachedfCompleted.IsSome then
            completed <- true
            cachedfCompleted.Value(cachedResult.Value)

    interface IServiceAsyncResult with
        member x.onCompleted fCompleted =
            returnInvoker.asyncInvoke <| fun() ->
                cachedfCompleted <- Some(fCompleted)
                this.tryToComplete()

// Dispatches every call on a registered service interface onto the thread that
// registered it: synchronously, or - for a method marked
// [<ServiceMethod(async=true)>] - by posting and handing back an
// IServiceAsyncResult.
//
// This was a System.Runtime.Remoting RealProxy until the move to .NET 10, where
// Remoting does not exist. DispatchProxy is its supported replacement and
// intercepts the same things (methods, property getters and setters, and event
// accessors), so the ~250 call sites through Services.* are unchanged. Two
// differences are worth knowing:
//
//   * DispatchProxy.Create requires a parameterless constructor, so the service
//     and the invoker are injected after construction rather than captured in
//     the constructor. ServiceProvider.register is the only thing that builds
//     one, and it sets both before the proxy is reachable.
//   * The invoker is captured at REGISTRATION time, exactly as the RealProxy
//     version captured it in its constructor. InvokerService.invoker is
//     ThreadStatic, so this is what binds a service to the thread that
//     registered it - the main thread for every service registered today.
type ServiceProxy() =
    inherit DispatchProxy()
    let attributeCache = new Dictionary<int, ServiceMethodAttribute>()

    member val private Service : obj = null with get, set
    member val private Invoker : Invoker = null with get, set

    member this.init(service: obj, invoker: Invoker) =
        this.Service <- service
        this.Invoker <- invoker

    member private this.serviceMethodAttributeCached(mi:MethodInfo) =
        let key = mi.MetadataToken
        lock this <| fun() ->
            if attributeCache.ContainsKey(key).not then
                let attributes = List2(mi.GetCustomAttributes(typeof<ServiceMethodAttribute>, true))
                let sma = attributes.map(fun(attr) -> attr.cast<ServiceMethodAttribute>()).tryHead.def(ServiceMethodAttribute())
                attributeCache.Add(key, sma)
            attributeCache.Item(key)

    // Unwraps the TargetInvocationException that reflection wraps around
    // anything the service itself throws, so callers keep seeing the exception
    // the service raised. The RealProxy path returned it through the message
    // and behaved the same way.
    member private this.invokeMethod(mi: MethodInfo, args: obj[]) =
        try
            mi.Invoke(this.Service, args)
        with :? TargetInvocationException as ex when not (isNull ex.InnerException) ->
            raise ex.InnerException

    member private this.doAsyncInvoke(mi: MethodInfo, args: obj[]) =
        let asyncResult = ServiceAsyncResult()

        this.Invoker.asyncInvoke <| fun() ->
            let result = this.invokeMethod(mi, args)
            asyncResult.complete(result)

        // void/unit returns nothing to wait on; anything else hands back the
        // result object the caller can subscribe to.
        if mi.ReturnType = typeof<unit> || mi.ReturnType = typeof<Void>
        then null
        else box(asyncResult)

    override this.Invoke(mi: MethodInfo, args: obj[]) =
        let sma = this.serviceMethodAttributeCached mi
        if sma.async then
            this.doAsyncInvoke(mi, args)
        else
            this.Invoker.invoke <| fun() -> this.invokeMethod(mi, args)

type ServiceProvider() =
    [<DefaultValue>]
    [<ThreadStatic>]
    static val mutable private _localServices : Dictionary<Type, obj>

    let services = new Dictionary<Type, obj>()
    
    static member localServices
        with get() =
            if ServiceProvider._localServices = null then
                ServiceProvider._localServices <- new Dictionary<Type, obj>()
            ServiceProvider._localServices

    member this.register(service:'a, wrap) =
        let service =
            if wrap then
                let proxy = DispatchProxy.Create<'a, ServiceProxy>()
                (box proxy :?> ServiceProxy).init(box service, InvokerService.invoker)
                box proxy
            else
                box(unbox<'a>(service))
        services.Add(typeof<'a>, service)

    member this.register(service:'a) = this.register(service, true)

    member this.registerLocal(service:'a) = 
        ServiceProvider.localServices.Add(typeof<'a>, service)

    member this.get<'a>() =
        let t = typeof<'a>
        let service = 
            if ServiceProvider.localServices.ContainsKey(t) then
                ServiceProvider.localServices.Item(t)
            else
                services.Item(t)
        unbox<'a>(service)

    member this.has<'a>() =
        let t = typeof<'a>
        ServiceProvider.localServices.ContainsKey(t) || services.ContainsKey(t)

type WtServiceProvider() =
    inherit ServiceProvider()
    member this.program = this.get<IProgram>()
    member this.desktop = this.get<IDesktop>()
    member this.managerView = this.get<IManagerView>()
    member this.filter = this.get<IFilterService>()
    member this.settings = this.get<ISettings>()
    member this.lm = this.get<ILicenseManager>()
    member this.dragDrop = this.get<IDragDrop>()
    member this.openResource(name) = Assembly.GetEntryAssembly().GetManifestResourceStream(name)
    member this.openIcon(name) = new Icon(this.openResource(name))
    member this.openImage(name) = System.Drawing.Image.FromStream(this.openResource(name))

[<AutoOpen>]
module GS =
    let Services = WtServiceProvider()
    