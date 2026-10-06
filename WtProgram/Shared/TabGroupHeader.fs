namespace Bemo

module TabGroupHeader =
    let formatKey count =
        if count = 1 then "TabGroupSizeSingularFormat" else "TabGroupSizePluralFormat"

    let append caption size =
        caption + " : " + size
