namespace Bemo
open System
open System.Drawing

// image is a newly owned surface unless the sprite implements this marker.
// Borrowed images must be cloned before composition and never disposed here.
type IBorrowedSpriteImage = interface end

type ISprite =
    abstract member image : Img
    abstract member children : List2<Pt * ISprite>
   
[<AutoOpen>]
module Sprite = 
    type ISprite with
        member this.render : Img =
            let source = this.image
            let image =
                match this with
                | :? IBorrowedSpriteImage -> source.clone()
                | _ -> source
            try
                use gr = image.graphics
                let draw (childLocation:Pt, child:ISprite) =
                    let childImage = child.render
                    try gr.DrawImageUnscaled(childImage.bitmap, childLocation.Point)
                    finally childImage.bitmap.Dispose()
                this.children.reverse.iter draw
                image
            with _ ->
                image.bitmap.Dispose()
                reraise()

        member this.tryHit(pt:Pt, path:List2<ISprite>) =
            let path = path.prepend(this)
            let hitPath = this.children.tryPick <| fun (location, child) ->
                let pt = pt.sub(location)
                child.tryHit(pt, path)
            match hitPath with
            | Some(path) -> Some(path)
            | None ->
                let image = this.image
                try
                    if image.containsPoint(pt) then Some(path) else None
                finally
                    match this with
                    | :? IBorrowedSpriteImage -> ()
                    | _ -> image.bitmap.Dispose()

        member this.hit pt = this.tryHit(pt, List2([])).def(List2([]))
