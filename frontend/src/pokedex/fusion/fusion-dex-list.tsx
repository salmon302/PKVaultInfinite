import { Badge, Card, Group, Stack, Text } from '@mantine/core';
import React from "react";
import { withErrorCatcher } from "../../error/with-error-catcher";
import { useStaticData } from "../../hooks/use-static-data";
import { useTranslate } from '../../translate/i18n';
import { SpeciesImg } from '../../img/species-img';
import { EntityContext } from '../../data/sdk/model';
import { useDexGetFusions, type FusionDexItemDTO } from '../../data/sdk/dex/dex-fusions';

const TypeBadge: React.FC<{ typeId: number }> = ({ typeId }) => {
    const staticData = useStaticData();
    const name = staticData?.types?.[String(typeId)]?.name ?? `#${typeId}`;
    return <Badge size='sm' variant='light'>{name}</Badge>;
};

const FusionCard: React.FC<{ item: FusionDexItemDTO }> = ({ item }) => (
    <Card withBorder padding='xs'>
        <Group gap='xs' wrap='nowrap'>
            <SpeciesImg species={item.headSpecies} context={EntityContext.Gen9} form={0} />
            <Text span c='dimmed'>/</Text>
            <SpeciesImg species={item.bodySpecies} context={EntityContext.Gen9} form={0} />
        </Group>
        <Text fw={500} mt={4}>{item.fusionName}</Text>
        <Text size='xs' c='dimmed'>{item.headName} + {item.bodyName}</Text>
        <Group gap={4} mt={4}>
            {item.types.map(t => <TypeBadge key={t} typeId={t} />)}
        </Group>
        <Group gap={4} mt={4}>
            {item.isSeen && <Badge color='blue' variant='outline'>Seen</Badge>}
            {item.isCaught && <Badge color='green' variant='outline'>Caught</Badge>}
        </Group>
    </Card>
);

export const FusionDexList: React.FC = withErrorCatcher("default", () => {
    const { t } = useTranslate();
    const { data, isPending } = useDexGetFusions();

    const merged = new Map<string, FusionDexItemDTO>();
    if (data?.data) {
        for (const entries of Object.values(data.data)) {
            for (const entry of entries) {
                const prev = merged.get(entry.id);
                if (!prev) {
                    merged.set(entry.id, entry);
                } else {
                    merged.set(entry.id, {
                        ...prev,
                        isSeen: prev.isSeen || entry.isSeen,
                        isCaught: prev.isCaught || entry.isCaught,
                    });
                }
            }
        }
    }

    const items = [...merged.values()].sort((a, b) =>
        a.isCaught === b.isCaught ? (a.isSeen === b.isSeen ? 0 : a.isSeen ? -1 : 1) : a.isCaught ? -1 : 1
    );

    return <Stack h='100%' style={{ flexGrow: 1, overflowY: 'scroll' }} p='md'>
        {isPending && <Text>{t('dex.list.loading')}</Text>}
        {!isPending && items.length === 0 && <Text c='dimmed'>{t('dex.list.empty')}</Text>}
        <Group style={{ alignContent: 'flex-start' }} gap='sm'>
            {items.map(item => <FusionCard key={item.id} item={item} />)}
        </Group>
    </Stack>;
});
