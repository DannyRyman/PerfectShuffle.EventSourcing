﻿namespace SampleApp
open PerfectShuffle.EventSourcing

module Main =
  
  [<EntryPoint>]
  let main argv = 

    let userStreamManager = MySampleApp.getUserStreamManager()

    let createUser name email pw =

      let evts =
        [|
          SampleApp.Events.UserCreated {Name = name; Email=email; Password=pw; Company = "Acme Corp"}            
        |] |> Array.map EventWithMetadata<_>.Wrap 

      async {
      let! evtProcessor = userStreamManager.GetEventProcessor email
      let! state = evtProcessor.ExtendedState()

      return!
        match state.State.User with
        | None ->
          async {
          printf "User %s does not exist, creating..." email        
          let batch = {StartVersion = state.NextExpectedStreamVersion; Events = evts }
          let! result = evtProcessor.Persist batch
          return result
          }
        | Some user ->
          async {
          printfn "User %s already exists, skipping..." email
          return Choice1Of2 state
          }
      }

    let changePw name newPw =

      let evts =
        [|
          SampleApp.Events.PasswordChanged newPw
        |] |> Array.map EventWithMetadata<_>.Wrap 

      async {
      let! evtProcessor = userStreamManager.GetEventProcessor(name)
      let! state = evtProcessor.ExtendedState()
      let batch = {StartVersion = state.NextExpectedStreamVersion; Events = evts }
      let! result = evtProcessor.Persist batch
      return result
      }

    let evts1 =
      async {
        do! createUser "James" "james@ciseware.com" "test123" |> Async.Ignore
        do! changePw "James" "test321" |> Async.Ignore
      }
    let evts2 =
      async {
        do! createUser "Tom" "tom@ciseware.com" "test123" |> Async.Ignore
        do! changePw "Tom" "test321" |> Async.Ignore
      }
    let evts3 =
      async {
        do! createUser "Fred" "fred@ciseware.com" "test123" |> Async.Ignore
        do! changePw "Fred" "test321" |> Async.Ignore
      }

    async {
      
      do! [|evts1;evts2;evts3|] |> Async.Parallel |> Async.Ignore

      let! streams = userStreamManager.Streams()
      let! processors = streams |> Seq.map userStreamManager.GetEventProcessor |> Async.Parallel
      let! users = processors |> Seq.map (fun x ->
        async {
        let! state = x.State()
        return state.User}) |> Async.Parallel
      for user in users do
        printfn "%A" user

    } |> Async.RunSynchronously

    System.Console.ReadKey() |> ignore
    printfn "%A" argv
    0 // return an integer exit code

      
  //    let eventProcessor = SampleApp.MySampleApp.initialiseEventProcessor()   

  //    while true do
  //      System.Console.ReadKey() |> ignore
  //      async {
  //      let email = sprintf "%d@test.com" System.DateTime.UtcNow.Ticks
  //
  //      let evts =
  //        [|
  //        for i = 1 to 1 do
  //          let name = sprintf "Test %d" i
  //          yield
  //            SampleApp.Events.UserCreated {Name = name; Email=email; Password="letmein"; Company = "Acme Corp"}
  //            |> EventWithMetadata<_>.Wrap 
  //        |]
  //
  //      let sw = System.Diagnostics.Stopwatch.StartNew()
  //
  //      let! state = eventProcessor.ExtendedState()
  //      let batch = { StartVersion = state.NextExpectedStreamVersion; Events = evts }       
  //      let! persistResult = eventProcessor.Persist batch
  //      match persistResult  with
  //      | Choice1Of2 currentState ->
  //        let users = currentState.State.Users
  //          
  //        printfn "Current users: %d" users.Count
  ////        for user in users do
  ////          printfn "%A" user.Value
  //      | Choice2Of2 e ->
  //        printfn "Something went terribly wrong: %A" e
  //      printfn "TIME to insert %d events: %dms" evts.Length sw.ElapsedMilliseconds
  //      } |> Async.RunSynchronously      
