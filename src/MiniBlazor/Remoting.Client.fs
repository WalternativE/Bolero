namespace MiniBlazor

open System
open System.IO
open System.Net.Http
open System.Runtime.CompilerServices
open System.Text
open FSharp.Reflection

type IRemoteService =
    abstract BasePath : string

type RemoteMethodDefinition =
    {
        Name: string
        FunctionType: Type
        ArgumentType: Type
        ReturnType: Type
    }

[<Extension>]
type RemotingExtensions =

    static member ExtractRemoteMethods(ty: Type) : Result<RemoteMethodDefinition[], list<string>> =
        if not (FSharpType.IsRecord ty) then
            Error [sprintf "Remote type must be a record: %s" ty.FullName]
        else
        let fields = FSharpType.GetRecordFields(ty, true)
        (fields, Ok [])
        ||> Array.foldBack (fun field res ->
            let fail fmt =
                let msg = sprintf fmt (ty.FullName + "." + field.Name)
                match res with
                | Ok _ -> Error [msg]
                | Error e -> Error (msg :: e)
            let ok x =
                res |> Result.map (fun l -> x :: l)
            if not (FSharpType.IsFunction field.PropertyType) then
                fail "Remote type field must be an F# function: %s"
            else
            let argTy, resTy = FSharpType.GetFunctionElements(field.PropertyType)
            if FSharpType.IsFunction resTy then
                fail "Remote type field must be an F# function with only one argument: %s. Use a tuple if several arguments are needed"
            elif not (resTy.IsGenericType && resTy.GetGenericTypeDefinition() = typedefof<Async<_>>) then
                fail "Remote function must return Async<_>: %s"
            else
            let resValueTy = resTy.GetGenericArguments().[0]
            ok {
                Name = field.Name
                FunctionType = field.PropertyType
                ArgumentType = argTy
                ReturnType = resValueTy
            }
        )
        |> Result.map Array.ofList

    static member Send(client: HttpClient, method: HttpMethod, requestUri: string, content: obj) =
        let content =
            match content with
            | null ->
                Json.Raw.Stringify Json.Null
            | content ->
                Json.GetEncoder (content.GetType()) content
                |> Json.Raw.Stringify
        new HttpRequestMessage(method, requestUri,
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        )
        |> client.SendAsync
        |> Async.AwaitTask

    static member SendAndParse<'T>(client, method, requestUri, content) = async {
        let! resp = RemotingExtensions.Send(client, method, requestUri, content)
        let! respBody = resp.Content.ReadAsStreamAsync() |> Async.AwaitTask
        use reader = new StreamReader(respBody)
        return Json.Read<'T> reader
    }

    static member MakeRemoteProxy(ty: Type, http: HttpClient, baseUri: string ref) =
        match RemotingExtensions.ExtractRemoteMethods(ty) with
        | Error errors ->
            raise <| AggregateException(
                "Cannot create remoting handler for type " + ty.FullName,
                [| for e in errors -> exn e |])
        | Ok methods ->
            let ctor = FSharpValue.PreComputeRecordConstructor(ty, true)
            methods
            |> Array.map (fun method ->
                let post =
                    typeof<RemotingExtensions>.GetMethod("SendAndParse")
                        .MakeGenericMethod([|method.ReturnType|])
                FSharpValue.MakeFunction(method.FunctionType, fun arg ->
                    let uri = !baseUri + method.Name
                    post.Invoke(null, [|http; HttpMethod.Post; uri; arg|])
                )
            )
            |> ctor

    static member NormalizeBasePath(basePath: string) =
        basePath + (if basePath.EndsWith "/" then "" else "/")

    [<Extension>]
    static member Remote<'T>(this: IElmishProgramComponent, basePath: string) =
        let basePath = RemotingExtensions.NormalizeBasePath(basePath)
        RemotingExtensions.MakeRemoteProxy(typeof<'T>, this.Http, ref basePath) :?> 'T

    [<Extension>]
    static member Remote<'T when 'T :> IRemoteService>(this: IElmishProgramComponent) =
        let basePath = ref ""
        let proxy = RemotingExtensions.MakeRemoteProxy(typeof<'T>, this.Http, basePath) :?> 'T
        basePath := RemotingExtensions.NormalizeBasePath proxy.BasePath
        proxy