﻿/// The idea:
/// We want to control another ivr independently of the current ivr we are running now.
/// The coordination should not be processed via channels, because it does not have to
/// pollute the trace and should not require a host.
///
/// There are two ivrs, The sideshow ivr and the control ivr. The control ivr can start and
/// replace the sideshow ivr. The control ivr receives the propagated errors from the side
/// show ivr. When a sideshow ivr is started or replaced, the replace command does not
/// return until either the sideshow is in a waiting state or completed. This guarantees that
/// the control ivr can be sure that all teardown of the old sideshow ivr and startup of the
/// new one is processed when the control is returned to it.

[<RequireQualifiedAccess>]
module IVR.Sideshow

open IVR.GlobalExports

/// This is the control for the side show.

type Control<'state>(doBegin: 'state * unit ivr -> unit ivr, getState: unit -> 'state option ivr) =
    /// Cancel and replace the side show ivr that is tagged with the given state. 
    /// This ivr returns after the
    /// currently running side show ivr is cancelled and the new one is started (gets into
    /// a waiting state or completed). Errors are propagated to the ivr of the caller.
    member this.Begin(ivr: unit ivr) : unit ivr =
        this.Begin(Unchecked.defaultof<'state>, ivr)
    member this.Begin(state, ivr) = doBegin(state, ivr)
    member this.State with get() = getState()

type State<'state> = 
    | Active of 'state
    | Idle

[<AutoOpen>]
module private Private = 
    let generateId = Ids.newGenerator().GenerateId

    [<NoEquality;NoComparison>]
    type Request<'state> = 
        | Replace of Id * ('state * unit ivr)
        | GetState of Id

    type 'r flux = 'r IVR.flux

/// Attach a sideshow to a control ivr that can control a sideshow ivr.
let attachTo (control: Control<'state> -> 'r ivr) : 'r ivr =

    let communicationId = generateId()

    let replace (state : 'state, toReplace : unit ivr) : unit ivr = 
        fun () ->
            let request = Replace(communicationId, (state, toReplace))

            let processResponse (response: obj IVR.result) =
                match response with
                | IVR.Value _ -> IVR.Value ()
                | IVR.Error e -> IVR.Error e
                | IVR.Cancelled -> IVR.Cancelled
                |> IVR.Completed

            IVR.Requesting (request, processResponse)

    let getState() : 'state option ivr = 
        fun () ->
            let request : Request<'state> = GetState(communicationId)

            let processResponse (response: obj IVR.result) =
                match response with
                | IVR.Value v -> IVR.Value (unbox v)
                | IVR.Error e -> IVR.Error e
                | IVR.Cancelled -> IVR.Cancelled
                |> IVR.Completed

            IVR.Requesting(request, processResponse)            

    let sideshowControl = Control<'state>(replace, getState)

    let isOurs = function
        | Replace(cid, _)
        | GetState(cid) when communicationId = cid 
            -> true
        | _ -> false

    let idleSideshow = IVR.Completed (IVR.Value ())

    // tbd: Flux module candidate!
    let cancelAndContinueWith continuation flux =
        let rec cancel flux =
            match flux with
            | IVR.Waiting _ ->
                IVR.tryCancel flux |> cancel
            | IVR.Requesting(r, cont) ->
                IVR.Requesting(r, cont >> cancel)
            | IVR.Completed result ->
                continuation result

        cancel flux

    // we wrap the control ivr so that we can process the requests.

    fun () ->
        let control = 
            control sideshowControl 
            |> IVR.start

        let rec next (sideshow: 'state option * unit flux) (control: 'r flux) = 
            // sideshow has priority, so run it as long we can.
            let state, flux = sideshow
            match flux with 
            | IVR.Requesting(r, cont) -> 
                IVR.Requesting(r, cont >> fun flux -> next (state, flux) control)
            | _ ->
            // sideshow is either waiting or completed, proceed with the control ivr
            match control with
            | IVR.Requesting(:? Request<'state> as request, cont) when isOurs request ->
                match request with
                | Replace(_, newSideshow) ->
                    beginSideshow sideshow newSideshow cont
                | GetState(_) ->
                    cont (IVR.Value (box state)) 
                    |> next sideshow
                    
            | IVR.Requesting(r, cont) ->
                IVR.Requesting(r, cont >> next sideshow)
            | IVR.Completed cResult ->
                flux |> cancelAndContinueWith (fun sResult ->
                    IVR.Completed <| 
                        // be sure errors of the control ivr have precendence!
                        match cResult, sResult with
                        | IVR.Error c, _ -> IVR.Error c
                        | _, IVR.Error s -> IVR.Error s
                        | _ -> cResult
                    )
            | IVR.Waiting cont ->
                IVR.Waiting (fun event ->
                    match flux with
                    | IVR.Waiting sideshowCont ->
                        // deliver the event to both.
                        next (state, sideshowCont event) (cont event)
                    | _ ->
                        next sideshow (cont event))

        and beginSideshow sideshow (newState, newSideshow) contControl =

            // start the new sideshow (until we are in a waiting or completed state)
            let rec startNew (state, sideshow) =
                match sideshow with
                | IVR.Requesting(r, cont) ->
                    IVR.Requesting(r, cont >> (fun flux -> startNew(state, flux)))
                | IVR.Waiting _ ->
                    contControl (IVR.Value (box ()))
                    |> next (state, sideshow)
                | IVR.Completed result ->
                    result 
                    |> IVR.Result.map box
                    |> contControl
                    |> next (None, idleSideshow)

            snd sideshow
            |> cancelAndContinueWith
                (function
                | IVR.Error err ->
                    // sideshow was in error before or after the cancellation, 
                    // we propagate this to the Replace invoker and ignore the
                    // new sideshow
                    IVR.Error err
                    |> contControl
                    |> next (None, idleSideshow)

                | _ -> startNew (Some newState, newSideshow()))

        next (None, idleSideshow) control

type Control = Control<unit>

module Extensions = 
    module IVR =
        let withSideshow = attachTo

[<assembly:AutoOpen("IVR.Sideshow.Extensions")>]
do ()

