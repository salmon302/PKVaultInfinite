import { Group, SegmentedControl } from '@mantine/core';
import React, { useState } from "react";
import { withErrorCatcher } from '../error/with-error-catcher';
import { PokedexMainWrapperDetails } from '../pokedex/details/pokedex-main-wrapper-details';
import { FiltersCard } from "../pokedex/filters/filters-card";
import { PokedexList } from "../pokedex/list/pokedex-list";
import { FusionDexList } from "../pokedex/fusion/fusion-dex-list";
import { FusionDexSyncAction } from "../pokedex/fusion/fusion-dex-sync";
import { useSpriteSizeLocalStorage } from '../ui/local-storage/use-storage-size-local-storage';
import { UIPokedexContent } from '../ui/pokedex/ui-pokedex-content';
import { UISpriteSizeWrapper } from '../ui/sprite-img/ui-sprite-size-wrapper';
import { useTranslate } from '../translate/i18n';

export const PokedexPage: React.FC = withErrorCatcher('default', () => {
  const [ speciesSize ] = useSpriteSizeLocalStorage('pokedex-sprite-size');
  const { t } = useTranslate();
  const [ view, setView ] = useState<'species' | 'fusions'>('species');

  return <UIPokedexContent>
    <Group justify='flex-start' mb='xs'>
      <SegmentedControl
        value={view}
        onChange={(value) => setView(value as 'species' | 'fusions')}
        data={[
          { label: t('dex.tab.species'), value: 'species' },
          { label: t('dex.tab.fusions'), value: 'fusions' },
        ]}
      />
      {view === 'fusions' && <FusionDexSyncAction />}
    </Group>

    {view === 'species' && <UISpriteSizeWrapper
      speciesSize={speciesSize}
      component={Group}
      mah='100%' align='flex-start' wrap='nowrap'
    >
      <FiltersCard mah='100%' w={300} style={{ flexShrink: 0 }} />

      <PokedexMainWrapperDetails>
        <PokedexList />
      </PokedexMainWrapperDetails>
    </UISpriteSizeWrapper>}

    {view === 'fusions' && <FusionDexList />}
  </UIPokedexContent>;
});
