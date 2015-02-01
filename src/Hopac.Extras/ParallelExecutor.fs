﻿namespace Hopac.Extras

open Hopac
open Hopac.Infixes
open Hopac.Job.Infixes
open Hopac.Alt.Infixes

/// Worker execution error.
type WorkerError<'error> =
    /// The message which causes this error is queued to `failedMessages` queue 
    /// and is executed later.
    | Recoverable of 'error
    /// The message which causes this error is not executed again. 
    | Fatal of 'error


/// Distributes messages among up to `degree` `worker`s which run in parallel. Degree of parallelism can be 
/// dynamically changed. If `worker` returns `Recoverable` WorkerError as a result, the message is queued to
/// special `failedMessages` Mailbox which is used as an alternative source of messages, i.e. messages are 
/// taken from `source` and `failedMessages` non deterministically. 
type ParallelExecutor<'msg, 'error>
    (
        degree: uint16,
        source: Alt<'msg>, 
        worker: 'msg -> Job<Choice<unit, WorkerError<'error>>>,
        ?completed: Mailbox<'msg * Choice<unit, WorkerError<'error>>>
    ) =
    let setDegree = ch<uint16>() 
    let workDone = ch<Choice<unit, WorkerError<_>>>()
    let failedMessages = mb()
     
    let pool = Job.iterateServer (degree, 0u)  <| fun (degree, usage) ->
        let setDegreeAlt() = setDegree |>>? fun degree -> degree, usage
        let workDoneAlt() = workDone |>>? fun _ -> degree, usage - 1u

        let processMessageAlt() =
            source <~>? failedMessages >>=? fun msg ->
                printfn "[R] got %O" msg
                job {
                    let! result = worker msg
                    printfn "[R] worker result: %A" result
                    return! Job.delay <| fun _ ->
                        match result with
                        | Fail (Recoverable _) -> failedMessages <<-+ msg
                        | Fail (Fatal _)
                        | Ok ->
                            match completed with
                            | Some mb ->    
                                printf "[R] sending %O to completed..." msg
                                mb <<-+ (msg, result)
                                >>% printfn "done."
                            | None -> Job.unit()
                        >>. (workDone <-- result) }
                |> Job.queue
            >>% (degree, usage + 1u)

        if usage < uint32 degree then
            setDegreeAlt() <|>? workDoneAlt() <|>? processMessageAlt()
        else 
            setDegreeAlt() <|>? workDoneAlt()
    do start pool
    /// Sets new degree of parallelism.
    member __.SetDegree value = setDegree <-+ value |> run