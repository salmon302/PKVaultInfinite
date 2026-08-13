import React from "react";
import { SpeciesImg, type SpeciesImgProps } from "./species-img";
import { EntityContext } from "../data/sdk/model";

export type FusionSpriteProps = {
    headSpecies: number;
    bodySpecies: number;
} & Pick<SpeciesImgProps, "isFemale" | "isShiny" | "isEgg" | "isShadow">;

/**
 * Pokémon Infinite Fusion: render the head and body species as a horizontal split
 * (head on the left, body on the right) overlapping in the middle to suggest the
 * merged fusion. Individual species are displayed under Gen9 so their SV sprites resolve.
 */
export const FusionSprite: React.FC<FusionSpriteProps> = ({
    headSpecies,
    bodySpecies,
    isFemale,
    isShiny,
    isEgg,
    isShadow,
}) => (
    <div style={{ position: "relative", width: "100%", height: "100%", display: "flex", alignItems: "center", justifyContent: "center" }}>
        <div style={{ width: "70%", marginRight: "-36%", zIndex: 2 }}>
            <SpeciesImg species={headSpecies} context={EntityContext.Gen9} form={0} isFemale={isFemale} isShiny={isShiny} isEgg={isEgg} isShadow={isShadow} />
        </div>
        <div style={{ width: "70%", zIndex: 1 }}>
            <SpeciesImg species={bodySpecies} context={EntityContext.Gen9} form={0} isFemale={isFemale} isShiny={isShiny} isEgg={isEgg} isShadow={isShadow} />
        </div>
    </div>
);
