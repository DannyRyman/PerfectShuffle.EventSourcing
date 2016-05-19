﻿namespace PerfectShuffle.EventSourcing.AzureTableStorage

open System
open PerfectShuffle.EventSourcing
open PerfectShuffle.EventSourcing.Store
open Microsoft.WindowsAzure.Storage
open Microsoft.WindowsAzure.Storage.Table
open FSharp.Control
open Streamstone

module EventStore =
  open System.Net
  open Microsoft.WindowsAzure.Storage
  open Microsoft.WindowsAzure.Storage.Table

[<CLIMutable>]
type EventEntity =
  {    
    Id : Guid
    TypeName : string
    Payload : byte[]
    Version : int
  }

/// Implementation of an event repository that connects to Azure Table Storage
type AzureTableStream<'event>(storageCredentials:Auth.StorageCredentials, streamName:string, partitionName:string, serializer : Serialization.IEventSerializer<'event>) =
    
  let hash (str:string) =
    let sha = System.Security.Cryptography.SHA256.Create()
    let sha256 =
      System.Text.Encoding.UTF8.GetBytes(str)
      |> sha.ComputeHash
    let sb = System.Text.StringBuilder()
    sb.Append "T" |> ignore // table names have to start with a letter and the hash might not
    for b in sha256 do
      sb.Append (b.ToString("x2")) |> ignore
    let hashString = sb.ToString()
    hashString.[0..min (hashString.Length - 1) 62] // Truncate to 62 chars

  let tableName = hash streamName

  do
    let tableNameVerification = System.Text.RegularExpressions.Regex("^[A-Za-z][A-Za-z0-9]{2,62}$")
    assert (tableNameVerification.IsMatch tableName) // Please see storage naming rules at https://blogs.msdn.microsoft.com/jmstall/2014/06/12/azure-storage-naming-rules/"

  let tableClient =
    let cloudStorageAccount = CloudStorageAccount(storageCredentials, true)
    let tableClient = cloudStorageAccount.CreateCloudTableClient()
    tableClient

  let table = tableClient.GetTableReference(tableName)
  
  let createTableTask =
    async {
    do! table.CreateIfNotExistsAsync() |> Async.AwaitTask |> Async.Ignore
    } |> Async.StartAsTask
  
  let partition = Partition(table, partitionName)

  let timeBeforeNextFetchWhenEndOfStreamReached = (TimeSpan.FromMilliseconds 100.0)
  let maxTimeBetweenFetches = (TimeSpan.FromSeconds 5.0)  

//  // TODO: Continue reading stream forever. If we get to end of stream wait some amount of time then try reading again (exponential back-off with a cap?)
//  let rawEventStream =    
//    let sliceSize = 100
//    let rec rawEventStreamAux startVersion (timeout:System.TimeSpan) =
//      // TODO: Why are we reading form startVersion when it's quite possible these events originated on this system?
//      // Can't we use readmodel position + 1 as the startVersion? Will need some refactoring.
//
//      asyncSeq {
//        let! result = Async.AwaitWaitHandle(readLatest, int timeout.TotalMilliseconds) // false if it timed out, true otherwise.
//        printfn "Reading from version %d" startVersion        
//        let! slice = Stream.ReadAsync<EventEntity>(partition, startVersion = startVersion, sliceSize = sliceSize) |> Async.AwaitTask      
//        printfn "READING VERSION %d" startVersion
//        for i = 0 to slice.Events.Length - 1 do
//          let evt = slice.Events.[i]
//          printfn "\tREAD EVT: (%d/%d) Version %d\n\tEvent ID: %A" i sliceSize evt.Version evt.Id
//          yield evt
//        
//        match slice.Events |> Array.tryLast with
//        | None ->
//          // Exponential backoff with an upper limit
//          let timeToWait = min (timeout.Add(timeout)) maxTimeBetweenFetches
//          printfn "Waiting %f seconds before retry" timeToWait.TotalSeconds
//          yield! rawEventStreamAux startVersion timeToWait
//        | Some evt ->
//          if slice.IsEndOfStream
//            then
//              yield! rawEventStreamAux (evt.Version + 1) timeBeforeNextFetchWhenEndOfStreamReached
//            else
//              yield! rawEventStreamAux (evt.Version + 1) TimeSpan.Zero
//    }
//    rawEventStreamAux 1 TimeSpan.Zero

  // TODO: Continue reading stream forever. If we get to end of stream wait some amount of time then try reading again (exponential back-off with a cap?)
  let rawEventStream startIndex =    
    let sliceSize = 100
    let rec rawEventStreamAux startVersion =
      asyncSeq {
        do! createTableTask |> Async.AwaitTask
        printfn "%s: Reading from version %d" streamName startVersion    
        let! foo = Stream.ExistsAsync(partition) |> Async.AwaitTask
        let! stream = Stream.TryOpenAsync(partition)|> Async.AwaitTask
        if stream.Found then
          let! slice = Stream.ReadAsync<EventEntity>(partition, startVersion = startVersion, sliceSize = sliceSize) |> Async.AwaitTask      
          printfn "%s READING VERSION %d" streamName startVersion
          for i = 0 to slice.Events.Length - 1 do
            let evt = slice.Events.[i]
            printfn "%s \tREAD EVT: (%d/%d) Version %d\n\tEvent ID: %A" streamName (i+1) slice.Events.Length evt.Version evt.Id
            yield evt
        
          match slice.Events |> Array.tryLast with
          | Some evt when not slice.IsEndOfStream ->
             yield! rawEventStreamAux (evt.Version + 1)
          | _ -> ()
    }
    rawEventStreamAux startIndex

  let deserializedEventStream startIndex =
    rawEventStream startIndex
    |> AsyncSeq.map (fun x ->
      let serializedEvent : Serialization.SerializedEvent =
        { 
          TypeName = x.TypeName
          Payload = x.Payload
        }
      let evt = serializer.Deserialize(serializedEvent)
      { StartVersion = x.Version; Events = [|evt|]})   

  let commit (concurrencyCheck:WriteConcurrencyCheck) (evts:EventWithMetadata<'event>[]) : Async<WriteResult> =
    async {      
      if evts.Length = 0
        then
          return Choice2Of2 (WriteFailure.NoItems)
        else
          let batchId = Guid.NewGuid()

          let eventsData =
            evts
            |> Seq.map (fun e ->
              let serializedEvent = serializer.Serialize e              
              e.Id, serializedEvent.TypeName, serializedEvent.Payload
            )
            |> Seq.map (fun (guid, typeName, payload) ->
              let props =
                [|
                  "Id", EntityProperty.GeneratePropertyForGuid(Nullable(guid))
                  "TypeName", EntityProperty.GeneratePropertyForString(typeName)
                  "Payload", EntityProperty.GeneratePropertyForByteArray(payload)
                |]
                |> dict
                |> EventProperties.From          
              EventData(EventId.From(guid),  props)
            )
            |> Seq.toArray

          let (|AggregateOrSingleExn|) (e:exn) =
            match e with
            | :? System.AggregateException as e -> (e.InnerExceptions |> Seq.toList)
            | x -> [x]

          let tryWrite =
            let newEventNum =
              match concurrencyCheck with
              | NoStream | EmptyStream | NewEventNumber 0 -> Some 0
              | NewEventNumber n -> Some n
              | Any -> None
            match newEventNum with
            | Some n ->
              async {
                try
                  let! result = Stream.WriteAsync(partition, n, eventsData) |> Async.AwaitTask
                  return Choice1Of2 (StreamVersion (result.Stream.Version))                   
                with
                  | AggregateOrSingleExn [:? ConcurrencyConflictException as e] ->
                    return WriteFailure.ConcurrencyCheckFailed |> Choice2Of2
                  |e ->
                    return WriteFailure.WriteException e |> Choice2Of2
              }
            | None ->
              async {
                try
                  let! stream = Stream.TryOpenAsync(partition) |> Async.AwaitTask
                  let stream =
                    match stream.Found, stream.Stream with
                    | true, stream -> stream
                    | false, _ -> Stream(partition)

                  let! result = Stream.WriteAsync(stream, eventsData) |> Async.AwaitTask              
                  return Choice1Of2 (StreamVersion (result.Stream.Version))       
                with
                  | AggregateOrSingleExn [:? ConcurrencyConflictException as e] ->
                    return WriteFailure.ConcurrencyCheckFailed |> Choice2Of2                    
                  |e ->
                    return WriteFailure.WriteException e |> Choice2Of2           
              }

          let! result = tryWrite                
          return result
  }

  interface IStream<'event> with
    member __.FirstVersion = 1
    member __.EventsFrom index = deserializedEventStream index
    member __.Save evts concurrencyCheck = commit concurrencyCheck evts