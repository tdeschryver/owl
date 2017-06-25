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
open System.Xml
open Newtonsoft.Json
open FSharp.Data

let logDirectory = "./logs"
let historyFile = sprintf "%s/history.csv" logDirectory
let endpointsFile = "endpoints.txt"
let refreshRate = 1000*60*10
let eventstore = new Event<byte[]>()
let logger = Targets.create Verbose [||]
let serverConfig =
  let port = "8083" |> Sockets.Port.Parse
  { defaultConfig with
      bindings = [ HttpBinding.create HTTP IPAddress.Loopback port ]
      logger = logger }

let mutable endpoints = List.empty<string>

let readEndpoints () =
  endpointsFile
    |> File.ReadLines
    |> Seq.filter (String.IsNullOrWhiteSpace >> not )
    |> Seq.toList

let checkXml(response: HttpWebResponse) =
  (response.ContentType = "text/xml" || response.ContentType = "application/xml") &&
  (
    try
      use data = response.GetResponseStream()
      use reader = new StreamReader(data)
      reader.ReadToEnd()
        |> XmlDocument().LoadXml
      true
    with
      | _ -> false
  )

let checkEndpoint (url: string) =
  async {
    try
      let req = WebRequest.Create(url)
      use! asyncResponse = req.AsyncGetResponse()
      let response = (asyncResponse :?> HttpWebResponse)

      let tester() = match url with
                     | url when url.EndsWith(".wsdl") -> checkXml(response)
                     | _ ->  true

      return (url, response.StatusCode = HttpStatusCode.OK && tester())
    with
      :? WebException -> return (url, false)
  }

let createLogDirectory =
  Directory.CreateDirectory(logDirectory) |> ignore
  if not <| File.Exists(historyFile) then
    File.AppendAllLines(historyFile, ["Timestamp;Endoint"])

let logToConsole url =
  logger.log LogLevel.Warn (Message.eventX url) |> ignore
  url

let logToDailyFile (url) = 
  let filename = sprintf "%s/%s.txt" logDirectory (DateTime.Today.ToString("yyyy-MM-dd"))
  let filecontents = sprintf "[%s] %s" (DateTime.Now.ToString("HH:mm")) url
  File.AppendAllLines(filename, [filecontents])
  url

let logToCSV (url) =   
  let filecontents = sprintf "%s;%s" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) url
  File.AppendAllLines(historyFile, [filecontents])
  url

let log (url) = 
  url 
    |> logToConsole
    |> logToDailyFile
    |> logToCSV

let refresh () =
  try
    endpoints
      |> Seq.map checkEndpoint
      |> Async.Parallel
      |> Async.RunSynchronously
      |> Seq.map (fun (url, success) ->
        match success with
        | false ->
          log url |> ignore
          (url, success)
        | true ->
          (url, success)
        )
      |> dict
      |> JsonConvert.SerializeObject
      |> Encoding.ASCII.GetBytes
      |> eventstore.Trigger
  with
    | e -> logger.log LogLevel.Error (Message.eventX e.Message) |> ignore

// FileSystemWatcher ?
let watchEndpoints () =
  let newEndpoints = readEndpoints()
  match newEndpoints = endpoints with
    | true -> ignore()
    | false ->
      endpoints <- newEndpoints
      refresh()
      ignore()

let rec job f interval skipFirst =
  async {
    if not skipFirst then f()
    do! Async.Sleep(interval)
    return! job f interval false
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

createLogDirectory
server |> Async.Start
job watchEndpoints 1000 false |> Async.Start
job refresh refreshRate true |> Async.Start

let rec read (msg) =
  match msg with
  | "q" -> ignore()
  | "r" ->
    refresh()
    read("")
  | _ -> Console.ReadLine() |> read

Console.ReadLine() |> read