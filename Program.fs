open Fake
open Suave
open Suave.Successful
open Suave.Operators
open Suave.Filters
open Suave.RequestErrors
open Suave.Writers
open Suave.Web
open Suave.Logging
open Suave.Utils
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open System
open System.Net
open System.Text
open System.Threading
open System.IO
open Newtonsoft.Json

let refreshRate = 1000*60*10
let logger = Targets.create Verbose [||]
let serverConfig = 
  let port = getBuildParamOrDefault "port" "8083" |> Sockets.Port.Parse
  { defaultConfig with
      bindings = [ HttpBinding.create HTTP IPAddress.Loopback port ]
      logger = logger }

let mutable endpoints = List.empty<string>
let eventstore = new Event<byte[]>()
let readEndpoints () =
  endpoints <- "endpoints.txt" |> File.ReadLines |> Seq.toList

let checkEndpoint (url: string) =
  async {
    try
      let req = WebRequest.Create(url) 
      use! rsp = req.AsyncGetResponse()
      return (url, (rsp :?> HttpWebResponse).StatusCode = HttpStatusCode.OK)
    with
      :? WebException -> return (url, false)
  }

let refresh () =
  endpoints
    |> Seq.map checkEndpoint
    |> Async.Parallel
    |> Async.RunSynchronously  
    |> Seq.map (fun (url, success) -> 
      match success with
      | false -> 
        logger.log LogLevel.Warn (Message.eventX url) |> ignore
        (url, success)
      | true -> 
        (url, success)
      )
    |> dict
    |> JsonConvert.SerializeObject
    |> Encoding.ASCII.GetBytes
    |> eventstore.Trigger

let rec job f = 
  async {
    f()
    do! Async.Sleep(refreshRate)
    return! job(f)
  }

let ws (webSocket : WebSocket) =
  //TODO: MailboxProcessor?
  let notifyLoop = 
    async { 
      while true do 
        let! msg = Async.AwaitEvent eventstore.Publish
        let response = msg |> ByteSegment
        let! _ = webSocket.send Text response true
        return ()
    }
  
  let cts = new CancellationTokenSource()
  Async.Start(notifyLoop, cts.Token)

  fun (context: HttpContext) ->
    socket {
      let loop = ref true

      while !loop do
        let! m = webSocket.read()
        match m with
        | Text, data, true ->
          let str = UTF8.toString data
          match str with
          | "refresh" -> refresh()
          | _ -> ()
        | Ping, _, _ ->
          let emptyResponse = [||] |> ByteSegment
          do! webSocket.send Pong emptyResponse true
        | Close, _, _ -> 
          let emptyResponse = [||] |> ByteSegment
          do! webSocket.send Close emptyResponse true
          loop := false
        | _ -> ()
    }

let api =
  choose
    [ 
      GET >=> choose
        [ 
          path "/ws" >=> handShake ws
          path "/api/refresh" >=> request (fun r ->
            refresh()
            OK("I am refreshed!")
          )
        ]
      NOT_FOUND "Found no handlers"
    ]

let listening, server = startWebServerAsync serverConfig api

readEndpoints()
server |> Async.Start
job <| refresh |> Async.Start

let rec read (msg) =
  match msg with
  | "q" -> ignore()
  | "refresh" -> 
    readEndpoints() 
    refresh()
    read("")
  | _ -> Console.ReadLine() |> read

Console.ReadLine() |> read
