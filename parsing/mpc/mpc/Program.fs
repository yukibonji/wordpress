﻿// Copyright (c) Mårten Rånge.
// ----------------------------------------------------------------------------------------------
// This source code is subject to terms and conditions of the Microsoft Public License. A
// copy of the license can be found in the License.html file at the root of this distribution.
// If you cannot locate the  Microsoft Public License, please send an email to
// dlr@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
//  by the terms of the Microsoft Public License.
// ----------------------------------------------------------------------------------------------
// You must not remove this notice, or any other, from this software.
// ----------------------------------------------------------------------------------------------

// ----------------------------------------------------------------------------
// Parser framework
// ----------------------------------------------------------------------------

type ParseFailureTree =
    | Empty
    | Expected      of string
    | NotExpected   of string
    | Group         of ParseFailureTree list
    | Fork          of ParseFailureTree*ParseFailureTree

type ParseResult<'T> = ('T option)*(ParseFailureTree*int)*string*int

type Parser<'T> = string*int -> ParseResult<'T>

let Join (left : ParseFailureTree, leftPos : int) (right : ParseFailureTree, rightPos : int) =
    let c = leftPos.CompareTo rightPos
    match left, right, c with
    | _     , Empty , 0             -> left,leftPos
    | Empty , _     , 0             -> right,leftPos
    | _     , _     , 0             -> Fork (left,right),leftPos
    | _     , _     , _ when c < 0  -> right,rightPos
    | _                             -> left,leftPos

let inline Result v f str pos  : ParseResult<'T> = (v,f,str,pos)
let inline Success v f str pos : ParseResult<'T> = Result (Some v) f str pos
let inline Failure f str pos   : ParseResult<_>  = Result None f str pos

let Delay f : Parser<'T> = f ()

let Return v : Parser<'T> =
    fun (str,pos) ->
        Success v (Empty,pos) str pos

let Bind (t : Parser<'T>) (fu : 'T -> Parser<'U>) : Parser<'U> =
    fun (str,pos) ->
        let otv,tf,tstr,tpos = t (str,pos)
        match otv with
        | Some tv   ->
            let u = fu tv
            let ouv,uf,ustr,upos = u (tstr, tpos)
            Result ouv (Join tf uf) ustr upos
        | _ -> Failure tf tstr tpos

let inline (>>=) t fu = Bind t fu

type ParseBuilder() =
    member x.Delay (f)      = Delay f
    member x.Bind (t,fu)    = Bind t fu
    member x.Return (v)     = Return v

let parse = ParseBuilder()

let NotExpected_EOS = NotExpected   "EOS"
let Expected_EOS    = Expected      "EOS"

let Item : Parser<char> =
    fun (str,pos) ->
        if pos < str.Length then Success str.[pos] (Empty,pos) str (pos+1)
        else Failure (NotExpected_EOS,pos) str pos

let EOS : Parser<unit> =
    fun (str,pos) ->
        if pos >= str.Length then Success () (Empty,pos) str pos
        else Failure (Expected_EOS,pos) str pos

let Satisfy (expected : ParseFailureTree) (test : char->bool) : Parser<char> =
    let e = Group [expected; NotExpected_EOS]
    fun (str,pos) ->
        if pos < str.Length then
            let ch = str.[pos]
            if test ch then Success str.[pos] (Empty,pos) str (pos+1)
            else Failure (expected,pos) str pos
        else Failure (e,pos) str pos

let IsChar (ch : char) : Parser<char> =
    let test c      = ch = c
    let expected    = Expected (ch.ToString())
    Satisfy expected test

let IsAnyOf (anyOf : string) : Parser<char> =
    let cs          = anyOf.ToCharArray()
    let expected    = cs |> Array.map (fun ch -> Expected (ch.ToString())) |> List.ofArray |> Group
    let set         = cs |> Set.ofArray
    let test c      = set.Contains c
    Satisfy expected test

let Map (p : Parser<'T>) (map : 'T -> 'U) : Parser<'U> =
    parse {
        let! pr = p
        let result = map pr
        return result
    }

let inline (>>?) l r = Map l r

let DebugBreak (p : Parser<'T>) : Parser<'T> =
    fun (str,pos) ->
        if System.Diagnostics.Debugger.IsAttached then
            System.Diagnostics.Debugger.Break ()
        p (str,pos)

let rec Many (p : Parser<'T>) : Parser<'T list> =
    fun (str,pos) ->
        let opv,pf,pstr,ppos = p (str,pos)
        match opv with
        | Some pv ->
            let orv,rf,rstr,rpos = Many p (pstr, ppos)
            match orv with
            | Some rv  -> Success (pv::rv) (Join pf rf) rstr rpos
            | _ -> failwith "Many should always succeed"
        | _ -> Success [] pf str pos

let Many1 (p : Parser<'T>) : Parser<'T list> =
    parse {
        let! first  = p
        let! rest   = Many p
        return first::rest
    }

let OrElse (left : Parser<'T>) (right : Parser<'T>) : Parser<'T> =
    fun (str,pos) ->
        let olr,lf,lstr,lpos = left (str,pos)
        let orr,rf,rstr,rpos = right (str,pos)
        let jf = Join lf rf
        match olr, orr with
        | Some lr   , _         -> Success lr jf lstr lpos
        | _         , Some rr   -> Success rr jf rstr rpos
        | _                     -> Failure jf str pos

let inline (<|>) l r = OrElse l r

let SepBy (term : Parser<'T>) (separator : Parser<'S>) (combine : 'T -> 'S -> 'T -> 'T): Parser<'T> =
    let rec sb acc (str,pos) =
        let osr,sf,sstr,spos = separator (str, pos)
        match osr with
        | Some sr ->
            let onr,nf,nstr,npos = term (sstr,spos)
            match onr with
            | Some nr ->
                let newacc = combine acc sr nr
                sb newacc (nstr,npos)
            | _ -> Failure (Join sf nf) nstr npos
        | _ -> Success acc sf str pos
    parse {
        let! first  = term
        let! result = sb first
        return result
    }

type ParseFailure =
    | IsExpecting       of string
    | IsNotExpecting    of string

let Run (parser : Parser<'T>) (str : string) =
    let rec collapse acc t =
        match t with
        | Empty         -> acc
        | Expected e    -> (IsExpecting e)::acc
        | NotExpected e -> (IsNotExpecting e)::acc
        | Group g       -> g |> List.fold collapse acc
        | Fork (l,r)    ->
            let lacc = collapse acc l
            let racc = collapse lacc r
            racc

    let prettify (fs : ParseFailure list) str pos =
        let sb              = System.Text.StringBuilder(sprintf "Failed at position %d," pos)
        let isExpecting     = "was expecting"       , " or "
        let isNotExpecting  = "was not expecting"   , " nor "

        let groupFunction (f : ParseFailure) =
            match f with
            | IsExpecting _     -> isExpecting
            | IsNotExpecting _  -> isNotExpecting

        let groups = fs |> Seq.groupBy groupFunction |> Array.ofSeq
        for i in 0..(groups.Length-1) do
            let (description,last),gfs = groups.[i]
            let prepend =
                match i with
                | 0                         -> " "
                | _                         -> ", "
            ignore <| sb.Append prepend
            ignore <| sb.Append description
            let gfs = gfs |> Array.ofSeq
            for i in 0..(gfs.Length-1) do
                let gf = gfs.[i]
                let str =
                    match gf with
                    | IsExpecting v     -> v
                    | IsNotExpecting v  -> v
                let prepend =
                    match i with
                    | 0                         -> " "
                    | _ when i = gfs.Length-1   -> last
                    | _                         -> ", "
                ignore <| sb.Append prepend
                ignore <| sb.Append str

        ignore <| sb.Append '.'
        sb.ToString ()

    let orv,(rf, rfpos),rstr,rpos = parser (str,0)
    match orv with
    | Some rv -> Some rv,"Parse successful",[],rstr,rpos
    | _ ->
        let cfs = collapse [] rf
        let dfs = cfs |> Seq.distinct |> List.ofSeq
        let msg = prettify dfs rstr rfpos
        None,msg,dfs,rstr,rpos

// ----------------------------------------------------------------------------
// Parser implementation for syntax like:x+y*(3+y)
// ----------------------------------------------------------------------------

(*
let TwoItem : Parser<char*char> =
    Item >>= fun first ->
        Atom >>= fun second ->
            Return (first,second)

let TwoItem : Parser<char*char> =
    parse {
        let! first  = Item
        let! second = Item
        return first,second
    }
*)

// Abstaction Syntax Tree

type BinaryOperation =
    | Add
    | Subtract
    | Multiply
    | Divide

type AbstractSyntaxTree =
    | Integer           of int
    | Identifier        of string
    | BinaryOperation   of BinaryOperation*AbstractSyntaxTree*AbstractSyntaxTree

// Define parser

let Expected_Digit      = Expected      "digit"
let Expected_Letter     = Expected      "letter"

let CharToBinaryOperator ch =
    match ch with
    | '+'   -> Add
    | '-'   -> Subtract
    | '*'   -> Multiply
    | '/'   -> Divide
    | _     -> failwith "Unexpected operator: %A" ch

let AddOrSubtract : Parser<BinaryOperation> =
    let p = IsAnyOf "+-"
    parse {
        let! ch = p
        return CharToBinaryOperator ch
    }

let MultiplyOrDivide : Parser<BinaryOperation> =
    let p = IsAnyOf "*/"
    parse {
        let! ch = p
        return CharToBinaryOperator ch
    }

let Digit : Parser<char> = Satisfy Expected_Digit System.Char.IsDigit

let SubExpr : Parser<AbstractSyntaxTree> ref = ref (Return (Integer 0))

let MatchedParentheses : Parser<AbstractSyntaxTree> =
    let pstart  = IsChar '('
    let pstop   = IsChar ')'
    parse {
        let! _      = pstart
        let! result = !SubExpr
        let! _      = pstop

        return result
    }

let Integer : Parser<AbstractSyntaxTree> =
    let pdigits = Many1 Digit
    parse {
        let! digits = pdigits
        let result =
            digits
            |> List.map (fun ch -> int ch - int '0')
            |> List.fold (fun s v -> 10*s + v) 0
        return Integer result
    }

let Identifier : Parser<AbstractSyntaxTree> =
    let pfirst  = Satisfy (Expected_Digit) System.Char.IsLetter
    let prest   = Many (Satisfy (Group [Expected_Digit;Expected_Letter]) System.Char.IsLetterOrDigit)
    parse {
        let! first  = pfirst
        let! rest   = prest
        let chars   = first::rest |> List.toArray
        let result  = System.String(chars)
        return Identifier result
    }

let CombineTerms l op r = BinaryOperation (op,l,r)

let Term : Parser<AbstractSyntaxTree> = Identifier <|> Integer <|> MatchedParentheses

let MultiplyOrDivideExpr : Parser<AbstractSyntaxTree> = SepBy Term MultiplyOrDivide CombineTerms

let AddOrSubtractExpr : Parser<AbstractSyntaxTree> = SepBy MultiplyOrDivideExpr AddOrSubtract CombineTerms

do
    SubExpr := AddOrSubtractExpr

let FullExpr : Parser<AbstractSyntaxTree> =
    parse {
        let! expr = AddOrSubtractExpr
        do! EOS
        return expr
    }

let rec Eval (lookup : string -> int) (ast : AbstractSyntaxTree) : int =
    match ast with
    | Integer               v   -> v
    | Identifier            id  -> lookup id
    | BinaryOperation (bop,l,r) ->
        let lv = Eval lookup l
        let rv = Eval lookup r
        match bop with
        | Add       -> lv + rv
        | Subtract  -> lv - rv
        | Multiply  -> lv * rv
        | Divide    -> lv / rv


// ----------------------------------------------------------------------------
// Main loop and test cases
// ----------------------------------------------------------------------------

let ColorPrint (cc : System.ConsoleColor) (prelude : string) (str : string) =
    let saved = System.Console.ForegroundColor
    System.Console.ForegroundColor <- cc
    try
        System.Console.Write prelude
        System.Console.WriteLine str
    finally
        System.Console.ForegroundColor <- saved

let PrintFailure = ColorPrint System.ConsoleColor.Red     "FAILURE: "
let PrintSuccess = ColorPrint System.ConsoleColor.Green   "SUCCESS: "

let ParserTests () =
    let x   = 3
    let y   = 5
    let z   = 7
    let abc = 11

    let lookup str =
        match str with
        | "x"   -> x
        | "y"   -> y
        | "z"   -> z
        | "abc" -> abc
        | _     -> System.Int32.MaxValue

    let tests =
        [|
            "abc"               , Some <| abc
            "123"               , Some <| 123
            "123+456"           , Some <| 123+456
            "x+y*3"             , Some <| x+y*3
            "x*(y+3*(z+x))"     , Some <| x*(y+3*(z+x))

            "a?"                , None
            "1?2"               , None
            "123+"              , None
            "123+#"             , None
            "x?y+3"             , None
            "(x+y"              , None
            "(x+y}"             , None
            "x+y)"              , None
        |]

    for test, expected in tests do
        let result, message, _, _, _ = Run FullExpr test
        match result, expected with
        | None, None ->
            PrintSuccess <| sprintf "Parsing failed as expected for '%s'\nMessage:%s" test message
        | Some ast, Some i ->
            let actual = Eval lookup ast
            if actual = i then
                PrintSuccess <| sprintf "Parsing and evaluation successful for '%s'\nExpected:%i\nActual:%i\nAST:%A" test i actual ast
            else
                PrintFailure <| sprintf "Parsing successful but evaluation failed for '%s'\nExpected:%i\nActual:%i\nAST:%A" test i actual ast
        | None, Some i ->
            PrintSuccess <| sprintf "Parsing failed for '%s'\nExpected:%i\nMessage:%s" test i message
        | Some ast, None ->
            PrintFailure <| sprintf "Parsing successful but expected to fail for '%s'\nAST:%A" test ast

[<EntryPoint>]
let main argv =
    ParserTests ()
    0
