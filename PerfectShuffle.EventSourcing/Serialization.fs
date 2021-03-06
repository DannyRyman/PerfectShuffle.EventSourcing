﻿namespace PerfectShuffle.EventSourcing

module Serialization =

  type IEventSerializer<'event> =
    abstract member Serialize : 'event -> SerializedEvent
    abstract member Deserialize : SerializedEvent -> 'event

  open System.IO
  
  module private JsonNet =

      open Newtonsoft.Json
      open Newtonsoft.Json.Serialization
          
      let s = new JsonSerializer()
      
      s.ContractResolver <- Newtonsoft.Json.Serialization.DefaultContractResolver ()      
      s.Formatting <- Formatting.Indented
      s.NullValueHandling <- NullValueHandling.Ignore
           
      let serialize o =
        use ms = new MemoryStream()
        (use jsonWriter = new JsonTextWriter(new StreamWriter(ms))
        s.Serialize(jsonWriter, o))
        let data = ms.ToArray()
        (o.GetType().AssemblyQualifiedName),data

      let deserialize (t, data:byte array) =
          use ms = new MemoryStream(data)
          use sr = new StreamReader(ms)
          use jsonReader = new JsonTextReader(sr)
          s.Deserialize(jsonReader, t)

  let CreateDefaultSerializer<'event>() = 
    { new IEventSerializer<'event> with
      member __.Serialize e =
        let typ,payload = box e |> JsonNet.serialize
        { TypeName = typ; Payload = payload}
      member __.Deserialize e =
        let t = System.Type.GetType(e.TypeName)
        JsonNet.deserialize (t, e.Payload) :?> _
      }