open Microsoft.Playwright
// Playwright is very heavy on task methods we'll need this
open System.Threading.Tasks
open FSharp.Control.Tasks
// This one is to write to disk
open System.IO
// Json serialization
open System.Text.Json
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
    let convertElementToPost (element: IElementHandle) =
        task {
            // steps 1, 2 y 3
            let! headerContent = element.QuerySelectorAsync(".title")
            let! author = element.QuerySelectorAsync(".subtitle a")
            let! content = element.QuerySelectorAsync(".content")
            // step 4
            let! title = headerContent.InnerTextAsync()
            let! authorText = author.InnerTextAsync()
            let! rawContent = content.InnerTextAsync()
            // step 5
            let summaryParts = rawContent.Split("...")

            let summary =
                // step 6
                summaryParts
                |> Array.tryHead
                |> Option.defaultValue ""

            // try to split the tags and the date
            let extraParts =
                // step 7
                (summaryParts
                 |> Array.tryLast
                 // we'll default to a single character string to ensure we will have
                 // at least an array with two elements ["", ""]
                 |> Option.defaultValue "\n")
                    .Split '\n'

            // split the tags given that each has a '#' and trim it, remove it if it's whitespace

            let tags =
                // step 7.1
                (extraParts
                 |> Array.tryHead
                 |> Option.defaultValue "")
                    .Split('#')
                // step 7.2
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (System.String.IsNullOrWhiteSpace >> not)

            let date =
                // step 7.3
                extraParts
                |> Array.tryLast
                |> Option.defaultValue ""

            printfn $"Parsed: {title} - {authorText}"
            // return el post
            return
                { title = title
                  author = authorText
                  tags = tags
                  summary = $"{summary}..."
                  date = date }
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
                with _ -> return ""
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
                
            return 
                {|
                    repName = pageTitle |> _.Trim() // |> regexStrip @"^Representative\s+"
                    contactlocation = contactlocation
                    phoneH = phoneH
                    phoneO = phoneO
                    phoneC = phoneC
                    email = email
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
    let writePostsToFile (getPosts: Task<Post array>) =
        task {
            let! posts = getPosts

            let opts =
                let opts = JsonSerializerOptions()
                opts.WriteIndented <- true
                opts

            let json =
                // serialize the array with the base class library System.Text.Json
                JsonSerializer.SerializeToUtf8Bytes(posts, opts)

            printfn "Saving to \"./posts.json\""
            return! File.WriteAllBytesAsync("./posts.json", json)
        }

open Utils
open Types
[<EntryPoint>]
let main _ =
    let root = @"https://ndlegis.gov"
    let basePage = $@"{root}/assembly/69-2025/regular/members"
    let getPWatURL basePage =
        Playwright.CreateAsync()
        |> getBrowser Firefox
        |> getPage basePage

    getPWatURL basePage
    |> getPostSummaries
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> Seq.map
        (fun p ->
            root + p
            |> getPWatURL
            |> getBioDetails
            |> fun x -> x
            |> Async.AwaitTask
            |> Async.RunSynchronously
            //|> Seq.toList
            |> fun x -> 
                System.Diagnostics.Debugger.Break()
                x
            |> ignore
        )
    |> Seq.toList
    |> ignore
    0