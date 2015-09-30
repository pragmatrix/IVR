﻿namespace IVR.Tests

open NUnit.Framework
open FsUnit

open System
open System.Collections.Generic
open IVR

//
// Basic Combinator and semantic tests.
//

type CancellationTracker() = 
    
    let mutable _disposed = false
    
    interface IDisposable with
        member this.Dispose() = _disposed <- true

    member this.disposed = _disposed;

type Event1 = Event1
type Event2 = Event2

[<TestFixture>]
type IVRTests() =

    let withoutHost = fun _ -> ()
    let start ivr = IVR.start withoutHost ivr
    let step ivr = IVR.step withoutHost ivr

    //
    // Automatic cancellation of Active ivrs.
    //

    [<Test>]
    member this.disposeIsCalledInASequentialIVR() = 
        let ct = new CancellationTracker()

        let a = ivr {
            use x = ct
            return 0
        }

        start a |> IVR.resultValue |> should equal 0
        ct.disposed |> should equal true


    [<Test>]
    member this.ivrIsCancelledInASequentialIVRSurroundingAWait() = 
        let ct = new CancellationTracker()

        let a = ivr {
            use x = ct
            do! IVR.waitFor' (fun (Event1) -> true)
            return 0
        }

        let started = start a
        ct.disposed |> should equal false
        step Event1 started |> ignore
        ct.disposed |> should equal true

    [<Test>]
    member this.``par: right ivr is cancelled when left throws an exception``() = 
        let ct = new CancellationTracker()

        let left = ivr {
            do! IVR.waitFor' (fun Event1 -> true)
            failwith "HellNo!"
        }

        let right = ivr {
            use ct = ct
            do! IVR.waitFor' (fun Event2 -> true)
        }

        IVR.par left right
        |> start
        |> step Event1 
        |> IVR.isError
        |> should equal true

        ct.disposed |> should equal true

    [<Test>]
    member this.``par: left ivr is cancelled when right throws an exception``() = 
        let ct = new CancellationTracker()

        let left = ivr {
            use ct = ct
            do! IVR.waitFor' (fun Event1 -> true)
        }

        let right = ivr {
            do! IVR.waitFor' (fun Event2 -> true)
            failwith "HellNo!"
        }

        IVR.par left right
        |> start
        |> step Event2
        |> IVR.isError
        |> should equal true

        ct.disposed |> should equal true

    [<Test>]
    member this.``par': right ivr is cancelled after left completes``() = 
        let ct = new CancellationTracker()

        let left = ivr {
            do! IVR.waitFor' (fun Event1 -> true)
        }

        let right = ivr {
            use ct = ct
            do! IVR.waitFor' (fun Event2 -> true)
        }

        let test = IVR.par' left right
        let started = start test
        step Event1 started |> ignore
        ct.disposed |> should equal true

    [<Test>]
    member this.``par': left ivr is cancelled after right completes``() = 
        let ct = new CancellationTracker()

        let left = ivr {
            use ct = ct
            do! IVR.waitFor' (fun (Event1) -> true)
        }

        let right = ivr {
            do! IVR.waitFor' (fun (Event2) -> true)
        }

        let test = IVR.par' left right
        let started = start test
        step Event2 started |> ignore
        ct.disposed |> should equal true


    [<Test>]
    member this.``par': left ivr is cancelled when the right one is completed after startup``() = 
        let ct = new CancellationTracker()

        let left = ivr {
            use ct = ct
            do! IVR.waitFor' (fun (Event1) -> true)
        }

        let right = ivr {
            return 0
        }

        let test = IVR.par' left right
        let started = start test
        IVR.isCompleted started |> should equal true
        ct.disposed |> should equal true

    [<Test>]
    member this.``lpar': first ivr is cancelled when the second one finishes first``() = 
        let ct = new CancellationTracker()

        let left = ivr {
            use ct = ct
            do! IVR.waitFor' (fun (Event1) -> true)
        }

        let right = ivr {
            do! IVR.waitFor' (fun (Event2) -> true)
        }

        let r = IVR.lpar' [left; right]
        let started = start r
        step Event2 started |> ignore
        ct.disposed |> should equal true

    [<Test>]
    member this.``lpar': second ivr is cancelled when the first one finishes first``() = 
        let ct = new CancellationTracker()

        let left = ivr {
            do! IVR.waitFor' (fun (Event1) -> true)
        }

        let right = ivr {
            use ct = ct
            do! IVR.waitFor' (fun (Event2) -> true)
        }

        let r = IVR.lpar' [left; right]
        let started = start r
        step Event1 started |> ignore
        ct.disposed |> should equal true

    [<Test>]
    member this.``computation expression: try finally handler is run on a regular completion``() =
        let mutable x = false

        let test = ivr {
            try
                do! IVR.waitFor' (fun Event1 -> true)
            finally
                x <- true
        }

        test
        |> start
        |> step Event1
        |> ignore

        x |> should equal true

    [<Test>]
    member this.``computation expression: try finally handler is run on an error completion``() =
        let mutable x = false

        let test = ivr {
            try
                do! IVR.waitFor' (fun Event1 -> true)
                failwith "Nooooo"
            finally
                x <- true
        }

        test
        |> start
        |> step Event1
        |> ignore

        x |> should equal true

    [<Test>]
    member this.``computation expression: try finally handler is run on an error completion at startup time``() =
        let mutable x = false

        let test = ivr {
            try
                failwith "Nooooo"
            finally
                x <- true
        }

        test
        |> start
        |> ignore

        x |> should equal true

    [<Test>]
    member this.``computation expression: handle exception at startup time``() =
        let mutable x = false

        let test = ivr {
            try
                failwith "Nooooo"
                return 0
            with e ->
                return 1
        }

        test
        |> start
        |> IVR.result
        |> should equal (Result 1)

    [<Test>]
    member this.``computation expression: handle exception at runtime``() =
        let mutable x = false

        let test = ivr {
            try
                do! IVR.waitFor' (fun Event1 -> true)
                failwith "Nooooo"
                return 0
            with e ->
                return 1
        }

        test
        |> start
        |> step Event1
        |> IVR.result
        |> should equal (Result 1)

    [<Test>]
    member this.``host properly cancels its ivr if it gets disposed asynchronously``() =
        let ct = new CancellationTracker()

        let ivr = ivr {
            use ct = ct
            do! IVR.waitFor' (fun (Event1) -> true)
        }

        let host = Host.newHost()

        async {
            try
                host.run ivr |> ignore
            with Cancelled ->
                ()
        } |> Async.Start

        (host :> IDisposable).Dispose();

        // wait a while... tbd: this makes this test brittle and should be fixed
        Async.Sleep(100) |> Async.RunSynchronously

        ct.disposed |> should equal true

    [<Test>]
    member this.``computation expression: yield sends a command to the host``() =
        let myHost = Queue()
        
        let test = ivr {
            yield 0
        } 

        test
        |> IVR.start myHost.Enqueue
        |> ignore

        myHost |> should equal [0]


    [<Test>]
    member this.``computation expression: yield sends combined commands to the host in the right order``() =
        let myHost = Queue()
        
        let test = ivr {
            yield 0
            yield 1
        } 

        test
        |> IVR.start myHost.Enqueue
        |> ignore

        myHost |> should equal [0;1]

