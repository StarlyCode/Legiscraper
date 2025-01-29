open Microsoft.Playwright
open System.Threading.Tasks
open FSharp.Control.Tasks
open System.IO
module Types =
    // Playwright offers different browsers so let's
    // declare a Discrimiated union with our choices
    type Browser =
        | Chromium
        | Chrome
        | Edge
        | Firefox
        | Webkit

        // let's also define a "pretty" representation of those
        member instance.AsString =
            match instance with
            | Chromium -> "Chromium"
            | Chrome -> "Chrome"
            | Edge -> "Edge"
            | Firefox -> "Firefox"
            | Webkit -> "Webkit"

    type Post =
        {
            title: string
            author: string
            summary: string
            tags: string array
            date: string
        }

module TextUtils =
    let regexStrip (pat: string) (inp: string) : string = System.Text.RegularExpressions.Regex.Replace(inp, pat, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
open TextUtils
module Utils =
    open Types
    let getBrowser (kind: Browser) (getPlaywright: Task<IPlaywright>) =
        task {
            // it's like we wrote
            // let playwright = await getPlaywright
            let! pl = getPlaywright

            printfn $"Browsing with {kind.AsString}"

            /// return! is like `return await`
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
            //  The first scrapping part, we'll get all of the elements that have
            // the "card-content" class
            let! bioLink = page.QuerySelectorAllAsync(".strong.member-name a")
            printfn $"Getting Cards from the landing page: {bioLink.Count}"
            return!
                bioLink
                // we'll convert the readonly list to an array
                |> Seq.toArray
                // we'll use the `Parallel` module to precisely process each post
                // in parallel and apply the `convertElementToPost` function
                |> Array.Parallel.map getLink
                // at this point we have a  Task<Post>[]
                // so we'll pass it to the next function to ensure all of the tasks
                // are resolved
                |> Task.WhenAll // return a Task<Post[]>
        }
    let getBioDetails (getPage: Task<IPage>) =
        task {
            let! page = getPage
            let! pageTitle = page.QuerySelectorAsync("h1.page-title") |> getContentTask
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
                
            return 
                {|
                    repName = pageTitle |> _.Trim() // |> regexStrip @"^Representative\s+"
                    contactlocation = contactlocation
                    phoneH = phoneH
                    phoneO = phoneO
                    phoneC = phoneC
                    email = email |> regexStrip @"^mailto:"
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
    let getPage (url: string) (getBrowser: Task<IBrowser>) =
        task {
            let! browser = getBrowser
            printfn $"Navigating to \"{url}\""

            // we'll get a new page first
            let! page = browser.NewPageAsync()
            // let's navigate right into the url
            let! res = page.GotoAsync url
            // we will ensure that we navigated successfully
            if not res.Ok then
                // we could use a result here to better handle errors, but
                // for simplicity we'll just fail of we couldn't navigate correctly
                return failwith "We couldn't navigate to that page"

            return page
        }
  
open Utils
open Types
[<EntryPoint>]
let main _ =
    let root = @"https://ndlegis.gov"
    let basePage ix = $@"{root}/assembly/69-2025/regular/members?page=%i{ix}"
    let browser = 
        Playwright.CreateAsync()
        |> getBrowser Firefox
    let getPWatURL basePage =
        browser
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
                    root + p
                    |> getPWatURL
                    |> getBioDetails
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                    |> fun x -> 
                        {| x with URL = url |}
                )
            //|> Seq.take 3
        )
    //|> Seq.take 1
    |> Seq.concat
    |> fun record -> 
        Csv.Seq.csv "," true (fun x -> x) record
        |> Seq.toList
        |> String.concat System.Environment.NewLine
        |> fun txt -> System.IO.File.WriteAllText("NDRepresentativeDownload.csv", txt)
    0