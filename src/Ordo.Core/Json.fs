module Ordo.Core.Json

open System.Text.Json
open System.Text.Json.Serialization
open FSharp.SystemTextJson

let configure (options: JsonSerializerOptions) =
    options.PropertyNameCaseInsensitive <- true
    let fsOptions = 
        JsonFSharpOptions.Default()
            .WithUnionAdjacentTag()
            .WithUnwrapOption()
            .WithUnionUnwrapSingleCaseUnions()
            .WithUnionAllowUnorderedTag()
    options.Converters.Add(JsonFSharpConverter(fsOptions))

let defaultOptions = 
    let options = JsonSerializerOptions()
    configure options
    options

let deserialise<'a> (data: string) =
    JsonSerializer.Deserialize<'a>(data, defaultOptions)

let tryDeserialise<'a> (data: string) =
    try
        Some (JsonSerializer.Deserialize<'a>(data, defaultOptions))
    with
    | _ -> None

let serialise<'a> (data: 'a) =
    JsonSerializer.Serialize(data, defaultOptions)
