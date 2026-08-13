import React from "react";
import type { MoveContainerValue } from '../move/move-container-fns';
import { useCurrentStorage } from '../panel/storage-panel-context';
import { UIStorageItem, type UIStorageItemProps } from '../../ui/storage/storage-item/ui-storage-item';
import { SpeciesImg, type SpeciesImgProps } from '../../img/species-img';
import { FusionSprite } from '../../img/fusion-sprite';




export type StorageItemProps =
  & Pick<UIStorageItemProps<MoveContainerValue>, 'id' | 'nodeId' | 'selected' | 'container' | 'name' | 'level' | 'slot' | 'onClick' | 'icons'>
  & Pick<SpeciesImgProps, 'species' | 'context' | 'form' | 'isFemale' | 'isShiny' | 'isEgg' | 'isShadow'>
  & { isFusion?: boolean; headSpecies?: number; bodySpecies?: number };

export const StorageItem: React.FC<StorageItemProps> = React.memo(({
  species,
  context,
  form,
  isFemale,
  isEgg,
  isShiny,
  isShadow,
  isFusion,
  headSpecies,
  bodySpecies,

  ...rest
}) => {
  const { storageIndex } = useCurrentStorage();

  const sprite = isFusion && headSpecies && bodySpecies
    ? <FusionSprite
        headSpecies={headSpecies}
        bodySpecies={bodySpecies}
        isFemale={isFemale}
        isShiny={isShiny}
        isEgg={isEgg}
        isShadow={isShadow}
      />
    : <SpeciesImg species={species} context={context} form={form} isFemale={isFemale} isShiny={isShiny} isEgg={isEgg} isShadow={isShadow} />;

  return (
    <UIStorageItem
      globalOrder={storageIndex * 1000 + rest.slot}
      {...rest}
    >
      {sprite}
    </UIStorageItem>
  );
});
