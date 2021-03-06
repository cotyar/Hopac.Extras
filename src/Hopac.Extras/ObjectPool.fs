﻿namespace Hopac.Extras

open Hopac
open Hopac.Infixes
open Hopac.Extensions
open System

type private PoolEntry<'a> = 
    { Value: 'a
      mutable LastUsed: DateTime }
    static member Create(value: 'a) = { Value = value; LastUsed = DateTime.UtcNow }
    interface IDisposable with
        member x.Dispose() = 
            // Mute exceptions those may be raised in instance's Dispose method to prevent the pool 
            // to stop looping.
            try 
                match box x.Value with
                | :? IDisposable as d -> d.Dispose()
                | _ -> ()
            with _ -> ()

/// Bounded pool of disposable objects. If number of given objects is equal to capacity then client will be blocked as it tries to get an instance. 
/// If an object in pool is not used more then inactiveTimeBeforeDispose period of time, it's disposed and removed from the pool. 
/// When the pool is disposing itself, it disposes all objects it caches first.
type ObjectPool<'a>(createNew: unit -> 'a, ?capacity: uint32, ?inactiveTimeBeforeDispose: TimeSpan) =
    let capacity = defaultArg capacity 50u
    let inactiveTimeBeforeDispose = defaultArg inactiveTimeBeforeDispose (TimeSpan.FromMinutes 1.)
    let reqCh = Ch<Promise<unit> * Ch<Result<PoolEntry<'a>, exn>>>()
    let releaseCh = Ch<'a PoolEntry>()
    let maybeExpiredCh = Ch<'a PoolEntry>()
    let doDispose = IVar()
    let hasDisposed = IVar()
    
    let rec loop (available: 'a PoolEntry list, given: uint32) = Job.delay <| fun _ ->
        // an instance returns to pool
        let releaseAlt() =
            releaseCh ^=> fun instance ->
                instance.LastUsed <- DateTime.UtcNow
                Job.start (timeOut inactiveTimeBeforeDispose >>=.
                           (maybeExpiredCh *<+ instance)) >>=.
                loop (instance :: available, given - 1u)
        // request for an instance
        let reqAlt() =
             reqCh ^=> fun (nack, replyCh) ->
                let ok available instance =
                    replyCh *<- Ok instance ^=>. loop (available, given + 1u) <|>
                    nack ^=>. loop (instance :: available, given)
                match available with
                | instance :: available -> ok available instance
                | [] -> try createNew () |> PoolEntry.Create |> ok available
                        with e -> (replyCh *<- Error e <|> nack) ^=>. loop (available, given)
        // an instance was inactive for too long
        let expiredAlt() =
            maybeExpiredCh ^=> fun instance ->
                if DateTime.UtcNow - instance.LastUsed > inactiveTimeBeforeDispose 
                   && List.exists (fun x -> obj.ReferenceEquals(x, instance)) available then
                    (instance :> IDisposable).Dispose()
                    loop (available |> List.filter (fun x -> not <| obj.ReferenceEquals(x, instance)), given)
                else loop (available, given)

        // the entire pool is disposing
        let disposeAlt() = 
            doDispose ^=> fun _ ->
                // dispose all instances that are in pool
                available |> List.iter (fun x -> (x :> IDisposable).Dispose())
                // wait until all given instances are returns to pool and disposing them on the way
                Job.forN (int given) (releaseCh >>- fun x -> (x :> IDisposable).Dispose()) >>=. hasDisposed *<= ()

        if given < capacity then
            // if number of given objects has not reached the capacity, synchronize on request channel as well
            releaseAlt() <|> expiredAlt() <|> disposeAlt() <|> reqAlt()
        else
            releaseAlt() <|> expiredAlt() <|> disposeAlt()
    
    do start (loop ([], 0u)) 
     
    let get() = reqCh *<+->- fun replyCh nack -> (nack, replyCh)

    /// Applies a function, that returns a Job, on an instance from pool. Returns `Alt` to consume 
    /// the function result.
    member __.WithInstanceJobResult (f: 'a -> #Job<Result<'r, exn>>) : Alt<Result<'r, exn>> =
        get() ^=> function
            | Ok entry ->
               Job.tryFinallyJobDelay
                   <| fun _ -> f entry.Value
                   <| releaseCh *<- entry
            | Error e -> Job.result (Error e)
    /// Applies a function, that returns a Job, on an instance from pool. Returns `Alt` to consume 
    /// the function result.
    member x.WithInstanceJob (f: 'a -> #Job<'r>) : Alt<Result<'r, exn>> = x.WithInstanceJobResult (f >> Job.catchResult)

    interface IAsyncDisposable with
        member __.DisposeAsync() = IVar.tryFill doDispose () >>=. hasDisposed

    interface IDisposable with 
        /// Runs disposing asynchronously. Does not wait until the disposing finishes. 
        member x.Dispose() = (x :> IAsyncDisposable).DisposeAsync() |> start

type ObjectPool<'a> with
    /// Applies a function on an instance from pool. Returns the function result.
    member x.WithInstance f = x.WithInstanceJob (fun a -> Job.result (f a))
    /// Returns an Async that applies a function on an instance from pool and returns the function result.
    member x.WithInstanceAsync f = x.WithInstance f |> Job.toAsync
    /// Applies a function on an instance from pool, synchronously, in the thread in which it's called.
    /// Warning! Can deadlock being called from application main thread.
    member x.WithInstanceSync f = x.WithInstance f |> run
