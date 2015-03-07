﻿[<AutoOpen>]
/// Contains infrastructure code and helper functions for FAKE's target feature.
module Fake.TargetHelper
    
open System
open System.Collections.Generic

/// [omit]
type TargetDescription = string

/// [omit]
type 'a TargetTemplate =
    { Name: string;
      Dependencies: string list;
      Description: TargetDescription;
      Function : 'a -> unit}
   
/// A Target can be run during the build
type Target = unit TargetTemplate

/// [omit]
let mutable PrintStackTraceOnError = false

/// [omit]
let mutable LastDescription = null
   
/// Sets the Description for the next target.
/// [omit]
let Description text = 
    if LastDescription <> null then 
        failwithf "You can't set the description for a target twice. There is already a description: %A" LastDescription
    LastDescription <- text

/// TargetDictionary
/// [omit]
let TargetDict = new Dictionary<_,_>()

/// Final Targets - stores final targets and if they are activated.
let FinalTargets = new Dictionary<_,_>()

/// BuildFailureTargets - stores build failure targets and if they are activated.
let BuildFailureTargets = new Dictionary<_,_>()

/// The executed targets.
let ExecutedTargets = new HashSet<_>()

/// The executed target time.
/// [omit]
let ExecutedTargetTimes = new List<_>()

/// Resets the state so that a deployment can be invoked multiple times
/// [omit]
let reset() = 
    TargetDict.Clear()
    ExecutedTargets.Clear()
    BuildFailureTargets.Clear()
    ExecutedTargetTimes.Clear()
    FinalTargets.Clear()

/// Returns a list with all target names.
let getAllTargetsNames() = TargetDict |> Seq.map (fun t -> t.Key) |> Seq.toList

/// Gets a target with the given name from the target dictionary.
let getTarget name = 
    match TargetDict.TryGetValue (toLower name) with
    | true, target -> target
    | _  -> 
        traceError <| sprintf "Target \"%s\" is not defined. Existing targets:" name
        for target in TargetDict do
            traceError  <| sprintf "  - %s" target.Value.Name
        failwithf "Target \"%s\" is not defined." name

/// Returns the DependencyString for the given target.
let dependencyString target =
    if target.Dependencies.IsEmpty then String.Empty else
    target.Dependencies 
      |> Seq.map (fun d -> (getTarget d).Name)
      |> separated ", "
      |> sprintf "(==> %s)"
    
/// Do nothing - fun () -> () - Can be used to define empty targets.
let DoNothing = (fun () -> ())

/// Checks whether the dependency can be added.
/// [omit]
let checkIfDependencyCanBeAdded targetName dependentTargetName =
    let target = getTarget targetName
    let dependentTarget = getTarget dependentTargetName

    let rec checkDependencies dependentTarget =
        dependentTarget.Dependencies 
          |> List.iter (fun dep ->
               if toLower dep = toLower targetName then 
                  failwithf "Cyclic dependency between %s and %s" targetName dependentTarget.Name
               checkDependencies (getTarget dep))
      
    checkDependencies dependentTarget
    target,dependentTarget

/// Adds the dependency to the front of the list of dependencies.
/// [omit]
let dependencyAtFront targetName dependentTargetName =
    let target,dependentTarget = checkIfDependencyCanBeAdded targetName dependentTargetName
    
    TargetDict.[toLower targetName] <- { target with Dependencies = dependentTargetName :: target.Dependencies }
  
/// Appends the dependency to the list of dependencies.
/// [omit]
let dependencyAtEnd targetName dependentTargetName =
    let target,dependentTarget = checkIfDependencyCanBeAdded targetName dependentTargetName
    
    TargetDict.[toLower targetName] <- { target with Dependencies = target.Dependencies @ [dependentTargetName] }

/// Adds the dependency to the list of dependencies.
/// [omit]
let dependency targetName dependentTargetName = dependencyAtEnd targetName dependentTargetName
  
/// Adds the dependencies to the list of dependencies.
/// [omit]
let Dependencies targetName dependentTargetNames = dependentTargetNames |> List.iter (dependency targetName)

/// Backwards dependencies operator - y is dependend on x.
let inline (<==) x y = Dependencies x y

/// Set a dependency for all given targets.
/// [omit]
[<Obsolete("Please use the ==> operator")>]
let TargetsDependOn target targets =
    getAllTargetsNames()
    |> Seq.toList  // work on copy since the dict will be changed
    |> List.filter ((<>) target)
    |> List.filter (fun t -> Seq.exists ((=) t) targets)
    |> List.iter (fun t -> dependencyAtFront t target)

/// Set a dependency for all registered targets.
/// [omit]
[<Obsolete("Please use the ==> operator")>]
let AllTargetsDependOn target = 
    let targets = getAllTargetsNames() 

    targets
    |> Seq.toList  // work on copy since the dict will be changed
    |> List.filter ((<>) target)
    |> List.filter (fun t -> Seq.exists ((=) t) targets)
    |> List.iter (fun t -> dependencyAtFront t target)
  
/// Creates a target from template.
/// [omit]
let targetFromTemplate template name parameters =    
    TargetDict.Add(toLower name,
      { Name = name; 
        Dependencies = [];
        Description = template.Description;
        Function = fun () ->
          // Don't run function now
          template.Function parameters })
    
    name <== template.Dependencies
    LastDescription <- null

/// Creates a TargetTemplate with dependencies-
let TargetTemplateWithDependecies dependencies body =
    { Name = String.Empty;
      Dependencies = dependencies;
      Description = LastDescription;
      Function = body}     
        |> targetFromTemplate

/// Creates a TargetTemplate.
let TargetTemplate body = TargetTemplateWithDependecies [] body 
  
/// Creates a Target.
let Target name body = TargetTemplate body name ()  

/// Represents build errors
type BuildError = { 
    Target : string
    Message : string }

let mutable private errors = []   
 
/// [omit]
let targetError targetName (exn:System.Exception) =
    closeAllOpenTags()
    errors <- 
        match exn with
            | BuildException(msg, errs) -> 
                let errMsgs = errs |> List.map(fun e -> { Target = targetName; Message = e }) 
                { Target = targetName; Message = msg } :: (errMsgs @ errors)
            | _ -> { Target = targetName; Message = exn.ToString() } :: errors
    let error e =
        match e with
        | BuildException(msg, errs) -> msg + (if PrintStackTraceOnError then Environment.NewLine + e.StackTrace.ToString() else "")
        | _ -> exn.ToString()

    let msg = sprintf "%s%s" (error exn) (if exn.InnerException <> null then "\n" + (exn.InnerException |> error) else "")
            
    traceError <| sprintf "Running build failed.\nError:\n%s" msg

    let isFailedTestsException = exn :? UnitTestCommon.FailedTestsException
    if not isFailedTestsException  then
        sendTeamCityError <| error exn

let addExecutedTarget target time =
    lock ExecutedTargets (fun () ->
        ExecutedTargets.Add (toLower target) |> ignore
        ExecutedTargetTimes.Add(toLower target,time) |> ignore
    )

/// Runs all activated final targets (in alphabetically order).
/// [omit]
let runFinalTargets() =
    FinalTargets
      |> Seq.filter (fun kv -> kv.Value)     // only if activated
      |> Seq.map (fun kv -> kv.Key)
      |> Seq.iter (fun name ->
           try             
               let watch = new System.Diagnostics.Stopwatch()
               watch.Start()
               tracefn "Starting FinalTarget: %s" name
               TargetDict.[toLower name].Function()
               addExecutedTarget name watch.Elapsed
           with
           | exn -> targetError name exn)

/// Runs all build failure targets.
/// [omit]
let runBuildFailureTargets() =
    BuildFailureTargets
      |> Seq.filter (fun kv -> kv.Value)     // only if activated
      |> Seq.map (fun kv -> kv.Key)
      |> Seq.iter (fun name ->
           try             
               let watch = new System.Diagnostics.Stopwatch()
               watch.Start()
               tracefn "Starting BuildFailureTarget: %s" name
               TargetDict.[toLower name].Function()
               addExecutedTarget name watch.Elapsed
           with
           | exn -> targetError name exn)


/// Prints all targets.
let PrintTargets() =
    log "The following targets are available:"
    for t in TargetDict.Values do
        logfn "   %s%s" t.Name (if isNullOrEmpty t.Description then "" else sprintf " - %s" t.Description)
              
/// <summary>Writes a dependency graph.</summary>
/// <param name="verbose">Whether to print verbose output or not.</param>
/// <param name="target">The target for which the dependencies should be printed.</param>
let PrintDependencyGraph verbose target =
    match TargetDict.TryGetValue (toLower target) with
    | false,_ -> PrintTargets()
    | true,target ->
        logfn "%sDependencyGraph for Target %s:" (if verbose then String.Empty else "Shortened ") target.Name
        let printed = new HashSet<_>()
        let order = new List<_>()
        let rec printDependencies indent act =
            let target = TargetDict.[toLower act]
            let addToOrder = not (printed.Contains (toLower act))
            printed.Add (toLower act) |> ignore
    
            if addToOrder || verbose then log <| (sprintf "<== %s" act).PadLeft(3 * indent)
            Seq.iter (printDependencies (indent+1)) target.Dependencies
            if addToOrder then order.Add act
        
        printDependencies 0 target.Name
        log ""
        log "The resulting target order is:"
        Seq.iter (logfn " - %s") order

/// Writes a summary of errors reported during build.
let WriteErrors () =
    traceLine()
    errors
    |> Seq.mapi(fun i e -> sprintf "%3d) %s" (i + 1) e.Message)
    |> Seq.iter(fun s -> traceError s)

/// <summary>Writes a build time report.</summary>
/// <param name="total">The total runtime.</param>
let WriteTaskTimeSummary total =    
    traceHeader "Build Time Report"
    if ExecutedTargets.Count > 0 then
        let width = 
            ExecutedTargetTimes 
              |> Seq.map (fun (a,b) -> a.Length) 
              |> Seq.max
              |> max 8

        let aligned (name:string) duration = tracefn "%s   %O" (name.PadRight width) duration
        let alignedError (name:string) duration = sprintf "%s   %O" (name.PadRight width) duration |> traceError

        aligned "Target" "Duration"
        aligned "------" "--------"
        ExecutedTargetTimes
          |> Seq.iter (fun (name,time) -> 
                let t = getTarget name
                aligned t.Name time)

        aligned "Total:" total
        if errors = [] then aligned "Status:" "Ok" 
        else 
            alignedError "Status:" "Failure"
            WriteErrors()
    else 
        traceError "No target was successfully completed"

    traceLine()

let private changeExitCodeIfErrorOccured() = if errors <> [] then exit 42 

/// [omit]
let isListMode = hasBuildParam "list"

/// Prints all available targets.
let listTargets() =
    tracefn "Available targets:"
    TargetDict.Values
      |> Seq.iter (fun target -> 
            tracefn "  - %s %s" target.Name (if target.Description <> null then " - " + target.Description else "")
            tracefn "     Depends on: %A" target.Dependencies)

// Instead of the target can be used the list dependencies graph parameter.
let doesTargetMeansListTargets target = target = "--listTargets"  || target = "-lt"

/// Determines a parallel build order for the given set of targets
let determineBuildOrder (d : TargetTemplate<unit>) =
    let levels = Dictionary<string, int>()
        
    // store the maximal level for each target
    let found (d : TargetTemplate<unit>) (level : int) =
        match levels.TryGetValue d.Name with
            | (true, old) ->
                if old < level then
                    levels.[d.Name] <- level
            | _ ->
                levels.[d.Name] <- level

    // traverse the given target-graph and store the maximal level
    // for each target.
    let rec traverse (graphs : seq<TargetTemplate<unit>>) (level : int) =
        for g in graphs do
            found g level

        let allDependencies = graphs |> Seq.collect (fun g -> g.Dependencies) |> Seq.distinct

        if not <| Seq.isEmpty allDependencies then
            traverse (allDependencies |> Seq.map getTarget) (level + 1)
  
    traverse [d] 0

    // the results are grouped by their level, sorted descending (by level) and 
    // finally grouped together in a list<TargetTemplate<unit>[]>
    let result = 
        levels |> Seq.map (fun (KeyValue(l,p)) -> (l,p))
               |> Seq.groupBy snd
               |> Seq.sortBy (fun (l,_) -> -l)
               |> Seq.map snd
               |> Seq.map (fun v -> v |> Seq.map fst |> Seq.distinct |> Seq.map getTarget |> Seq.toArray)
               |> Seq.toList

    result

/// starts the given number of threads working on targets in the queue.
let startTargetWorkers (parallelJobs : int) (bag : System.Collections.Concurrent.ConcurrentBag<Target>) (finished : System.Threading.CountdownEvent) =
    let cancel = new System.Threading.CancellationTokenSource()
    let ct = cancel.Token
    
    // worker thread function  
    let worker (threadState : obj) =
        try
            while true do
                ct.ThrowIfCancellationRequested()
                match bag.TryTake() with
                    | (true, target) -> 
                        try
                            traceStartTarget target.Name target.Description (dependencyString target)
                            let watch = new System.Diagnostics.Stopwatch()
                            watch.Start()
                            target.Function()
                            addExecutedTarget target.Name watch.Elapsed
                            traceEndTarget target.Name       
                        finally
                            finished.Signal() |> ignore
                    | _ ->
                        System.Threading.Thread.Sleep(100)
        with :? System.OperationCanceledException ->
            ()

    let workers =
        Array.init parallelJobs (fun i -> 
            let thread = new System.Threading.Thread(System.Threading.ThreadStart(worker), IsBackground = true)
            thread.Start()
            thread
        )

    workers, cancel

/// runs the given target-order in parallel using the given number of threads
let runParallelTargets (parallelJobs : int) (order : list<TargetTemplate<unit>[]>) =
    // create a concurrent bag for holding the targets
    // currently runnable.
    let bag = System.Collections.Concurrent.ConcurrentBag()
    let finished = new System.Threading.CountdownEvent(0)

    // start a number of worker threads running all targets
    // in the bag and signaling the finished event when done.
    let workers, cancel = startTargetWorkers parallelJobs bag finished

    // sequentially enqueue all parallel targets and wait for each
    // parallel "level".
    for par in order do
        finished.Reset(par.Length)
        for e in par do bag.Add e
        finished.Wait()

    // cancel the created threads
    cancel.Cancel()

    // release the synchronization stuff
    finished.Dispose()
    cancel.Dispose()

/// Runs a target and its dependencies.
let run targetName =            
    if doesTargetMeansListTargets targetName then listTargets() else
    if LastDescription <> null then failwithf "You set a task description (%A) but didn't specify a task." LastDescription
    let rec runTarget targetName =
        try      
            if errors = [] && ExecutedTargets.Contains (toLower targetName) |> not then
                let target = getTarget targetName      
      
                if hasBuildParam "single-target" then
                    traceImportant "Single target mode ==> Skipping dependencies."
                else
                    List.iter runTarget target.Dependencies
      
                if errors = [] then
                    traceStartTarget target.Name target.Description (dependencyString target)
                    let watch = new System.Diagnostics.Stopwatch()
                    watch.Start()
                    target.Function()
                    addExecutedTarget targetName watch.Elapsed
                    traceEndTarget target.Name                
        with
        | exn -> targetError targetName exn

    
    let watch = new System.Diagnostics.Stopwatch()
    watch.Start()        
    try
        tracefn "Building project with version: %s" buildVersion
        let parallelJobs = environVarOrDefault "fake-parallel-jobs" "1" |> int
      

        if parallelJobs > 1 then
            tracefn "Running parallel build with %d workers" parallelJobs

            // determine a parallel build order
            let order = determineBuildOrder (getTarget targetName)

            // run the target-order in parallel 
            runParallelTargets parallelJobs order

        else
            // single threaded build
            PrintDependencyGraph false targetName
            runTarget targetName

    finally
        if errors <> [] then
            runBuildFailureTargets()    
        runFinalTargets()
        killAllCreatedProcesses()
        WriteTaskTimeSummary watch.Elapsed
        changeExitCodeIfErrorOccured()

/// Registers a BuildFailureTarget (not activated).
let BuildFailureTarget name body = 
    Target name body
    BuildFailureTargets.Add(toLower name,false)

/// Activates the BuildFailureTarget.
let ActivateBuildFailureTarget name = 
    let t = getTarget name // test if target is defined
    BuildFailureTargets.[toLower name] <- true
 
/// Registers a final target (not activated).
let FinalTarget name body = 
    Target name body
    FinalTargets.Add(toLower name,false)

/// Activates the FinalTarget.
let ActivateFinalTarget name = 
    let t = getTarget name // test if target is defined
    FinalTargets.[toLower name] <- true