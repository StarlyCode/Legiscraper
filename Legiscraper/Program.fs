open Microsoft.Playwright
open System.Threading.Tasks
open FSharp.Control.Tasks
open System.IO
module Types =
    type Browser =
        | Chromium
        | Chrome
        | Edge
        | Firefox
        | Webkit

        member this.AsString =
            match this with
            | Chromium -> "Chromium"
            | Chrome -> "Chrome"
            | Edge -> "Edge"
            | Firefox -> "Firefox"
            | Webkit -> "Webkit"

module TextUtils =
    let regexStrip (pat: string) (inp: string) : string = System.Text.RegularExpressions.Regex.Replace(inp, pat, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
    let regexSplit (pat: string) (inp: string) : string list = System.Text.RegularExpressions.Regex.Split(inp, pat, System.Text.RegularExpressions.RegexOptions.IgnoreCase) |> Seq.toList
open TextUtils
module Utils =
    open Types
    let getBrowser (kind: Browser) (getPlaywright: Task<IPlaywright>) =
        task {
            let! pl = getPlaywright

            printfn $"Browsing with {kind.AsString}"
            return!
                match kind with
                | Chromium -> pl.Chromium.LaunchAsync()
                | Chrome ->
                    let opts = BrowserTypeLaunchOptions()
                    opts.Channel <- "chrome"
                    pl.Chromium.LaunchAsync(opts)
                | Edge ->
                    let opts = BrowserTypeLaunchOptions()
                    opts.Channel <- "msedge"
                    pl.Chromium.LaunchAsync(opts)
                | Firefox -> pl.Firefox.LaunchAsync()
                | Webkit -> pl.Webkit.LaunchAsync()
        }
    
    let getLink (element: IElementHandle) =
        task {
            return! element.GetAttributeAsync("href")
        }

    let getContent (element: IElementHandle) =
        task {
            return! element.TextContentAsync()
        }
    let doToNonNullElement defaultValue (fn: IElementHandle -> Task<'a>) (element: Task<IElementHandle>) =
        task {
            let! element2 = element
            if element2 = null then
                return defaultValue 
            else
                try
                    return! fn element2
                with _ -> return defaultValue
        }

    let getContentTask (element: Task<IElementHandle>) =
        doToNonNullElement "" (fun x -> x.TextContentAsync()) element
        
    let getLinkTask = doToNonNullElement "" (fun element -> element.GetAttributeAsync("href"))

    let getPostSummaries (getPage: Task<IPage>) =
        task {
            let! page = getPage
            let! bioLink = page.QuerySelectorAllAsync(".strong.member-name a")
            printfn $"Getting Cards from the landing page: {bioLink.Count}"
            return!
                bioLink
                |> Seq.toArray
                |> Array.Parallel.map getLink
                |> Task.WhenAll
        }
    let getBioDetails (getPage: Task<IPage>) =
        task {
            let! page = getPage
            let! contactlocation = page.QuerySelectorAsync(".field--name-field-person-contactlocation div.field__item") |> getContentTask
            let! phoneH = page.QuerySelectorAsync(".field--name-field-bio-phone-h div.field__item") |> getContentTask
            let! phoneO = page.QuerySelectorAsync(".field--name-field-bio-phone-o div.field__item") |> getContentTask
            let! phoneC = page.QuerySelectorAsync(".field--name-field-bio-phone-c div.field__item") |> getContentTask
            let! email = page.QuerySelectorAsync(".field--name-field-person-email2 a") |> getLinkTask 
            let! bioLines = 
                page.QuerySelectorAllAsync(".flexbox-item.biography-biography-wrapper li") 
            let! bioLines =
                bioLines
                |> Seq.map getContent
                |> Task.WhenAll
            let bioLines = bioLines |> Seq.toList

            let bioLine num = 
                bioLines
                |> List.tryItem num
                |> Option.defaultValue ""
            
            let! pageTitle = page.QuerySelectorAsync("h1.page-title") |> getContentTask
            let (title, personName) = 
                pageTitle 
                |> regexStrip @"^\s*"
                |> regexStrip @"\s*$"
                |> TextUtils.regexSplit @"\s+"
                |> function
                | [] -> "", ""
                | [x] -> x, ""
                | x :: xs -> x, String.concat " " xs
            
            return 
                {|
                    Name = personName
                    Location = contactlocation
                    PhoneHome = phoneH
                    PhoneOffice = phoneO
                    PhoneCell = phoneC
                    Title = title
                    Email = email |> regexStrip @"^mailto:"
                    bio1 = bioLine 0
                    bio2 = bioLine 1
                    bio3 = bioLine 2
                    bio4 = bioLine 3
                    bio5 = bioLine 4
                    bio6 = bioLine 5
                    bio7 = bioLine 6
                    bio8 = bioLine 7
                    bio9 = bioLine 8
                |}
        }
    let getPage (url: string) (page: IPage) =
        task {
            printfn $"Navigating to \"{url}\""
            let! res = page.GotoAsync url
            if not res.Ok then
                return failwith "We couldn't navigate to that page"
            return page
        }
  
open Utils
open Types
open System
[<EntryPoint>]
let main _ =
    let root = @"https://ndlegis.gov"
    let basePage ix = $@"{root}/assembly/69-2025/regular/members?page=%i{ix}"
    let awaitTask (task: Task<'a>) : 'a = task |> Async.AwaitTask |> Async.RunSynchronously
    let page = 
        Playwright.CreateAsync()
        |> getBrowser Firefox
        |> awaitTask
        |> fun x -> x.NewPageAsync()
        |> awaitTask
    let start = DateTime.Now
    let getPWatURL basePage =
        page
        |> getPage basePage
    [ 0 .. 8 ]
    |> Seq.map
        (fun ix -> 
            let url = basePage ix
            getPWatURL url
            |> getPostSummaries
            |> Async.AwaitTask
            |> Async.RunSynchronously
            |> Seq.map
                (fun p ->
                    let repURL = root + p
                    repURL
                    |> getPWatURL
                    |> getBioDetails
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                    |> fun x -> 
                        {| x with URL = repURL |}
                )
            //|> Seq.take 1
        )
    //|> Seq.take 1
    |> Seq.concat
    |> Seq.toList
    |> fun records -> 
        printfn $"Downloaded {records.Length} records in {(DateTime.Now - start).TotalSeconds} seconds"
        Csv.Seq.csv "," true (fun x -> x) records
        |> Seq.toList
        |> String.concat System.Environment.NewLine
        |> fun txt -> 
            let ts = System.DateTime.Now.Ticks
            System.IO.File.WriteAllText($"NDRepresentativeDownload_{ts}.csv", txt)
    0