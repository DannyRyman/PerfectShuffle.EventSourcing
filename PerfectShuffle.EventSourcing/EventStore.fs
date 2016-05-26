﻿namespace PerfectShuffle.EventSourcing

module Store =

  open System
  open System.Net
  open FSharp.Control

  type WriteConcurrencyCheck =
  /// This disables the optimistic concurrency check.
  | Any
  /// this specifies the expectation that target stream does not yet exist.
  | NoStream
  /// this specifies the expectation that the target stream has been explicitly created, but does not yet have any user events written in it.
  | EmptyStream
  /// Any other integer value	The event number that you expect the stream to currently be at.
  | NewEventNumber of int

  type WriteSuccess =
  | StreamVersion of int

  type WriteFailure =
  | NoItems
  | ConcurrencyCheckFailed
  | WriteException of exn

  type WriteResult = Choice<WriteSuccess, WriteFailure>

  type Batch<'event> =
    {
      StartVersion : int
      Events : EventToRecord<'event>[]
    }

  type IStream<'event> =
    abstract member FirstVersion : int
    abstract member EventsFrom : version:int -> AsyncSeq<EventWithMetadataAndVersion<'event>>
    abstract member Save : events:EventToRecord<'event>[] -> WriteConcurrencyCheck -> Async<WriteResult>

 