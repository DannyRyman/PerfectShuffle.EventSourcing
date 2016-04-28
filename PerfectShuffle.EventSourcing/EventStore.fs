﻿namespace PerfectShuffle.EventSourcing

module Store =

  open System
  open System.Net

  type WriteConcurrencyCheck =
  /// This disables the optimistic concurrency check.
  | Any
  /// this specifies the expectation that target stream does not yet exist.
  | NoStream
  /// this specifies the expectation that the target stream has been explicitly created, but does not yet have any user events written in it.
  | EmptyStream
  /// Any other integer value	The event number that you expect the stream to currently be at.
  | NewEventNumber of int

  type WriteResult =
  | Success
  | ConcurrencyCheckFailed
  | WriteException of exn

  type NewEventArgs<'TEvent> = {EventNumber : int; Event : EventWithMetadata<'TEvent>}

  type IEventRepository<'TEvent> =
    abstract member Events : IObservable<NewEventArgs<'TEvent>>
    abstract member Save : events:EventWithMetadata<'TEvent>[] -> WriteConcurrencyCheck -> Async<WriteResult>

 