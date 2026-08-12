import { Alert, Button, Group, type ComboboxItem, Popover } from '@mantine/core';
import { AlertTriangleIcon, CalendarSyncIcon, CheckIcon } from 'lucide-react';
import React, { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { useSaveInfosGetAll } from "../../data/sdk/save-infos/save-infos.gen";
import { useStaticData } from "../../hooks/use-static-data";
import { useTranslate } from "../../translate/i18n";
import { UIMultiSelect } from '../../ui/form/select/ui-multi-select';
import { UIFormCard } from '../../ui/popover/popover-card/ui-form-card';
import { UIGameImg } from '../../ui/sprite-img/ui-game-img';
import { useStorageDexFusionsSync } from '../../data/sdk/dex/dex-fusions';

export const FusionDexSyncAction: React.FC = () => {
    const { t } = useTranslate();
    const [ opened, setOpened ] = useState(false);

    const staticData = useStaticData();

    const saveInfosQuery = useSaveInfosGetAll();
    const saveInfos = saveInfosQuery.data?.data ?? {};

    const syncMutation = useStorageDexFusionsSync();

    const { handleSubmit, setValue, control } =
        useForm<{ saveIds: number[] }>({
            defaultValues: { saveIds: [] },
        });

    const [ saveIds = [] ] = useWatch({ control, name: [ 'saveIds' ] });

    const onSubmit = handleSubmit(async ({ saveIds }) => {
        const result = await syncMutation.mutateAsync(saveIds);
        if (result.status >= 400) {
            return;
        }
        setOpened(false);
    });

    return <Popover opened={opened} onChange={setOpened} position='bottom-end' withinPortal>
        <Popover.Target>
            <Button
                leftSection={<CalendarSyncIcon />}
                variant='default'
                loading={syncMutation.isPending}
            >{t('dex.tab.sync-fusions')}</Button>
        </Popover.Target>
        <Popover.Dropdown>
            <UIFormCard
                onSubmit={onSubmit}
                icon={<CalendarSyncIcon />}
                title={t('storage.fusion-dex-sync.title')}
                description={t("storage.fusion-dex-sync.description")}
                disabled={saveIds.length < 2}
                miw={350}
            >
                <UIMultiSelect
                    name='saveIds'
                    controlLabel={t('storage.fusion-dex-sync.controls-label')}
                    label={t("storage.fusion-dex-sync.title")}
                    value={saveIds.map(String)}
                    onChange={value => setValue('saveIds', value.map(Number))}
                    data={[
                        { value: '0', label: 'PKVault' },
                        ...Object.values(saveInfos).map((save): ComboboxItem => ({
                            value: save.id.toString(),
                            label: `${staticData.versions[ save.version ]?.name} - ${save.trainerName}`,
                        })),
                    ]}
                    renderOption={({ option, checked }) => {
                        if (!saveInfosQuery.data)
                            return null;
                        const saveId = +option.value;
                        const save = saveInfosQuery.data.data[ saveId ];
                        const name = save && staticData.versions[ save.version ]?.name;
                        return <Group wrap='nowrap'>
                            {checked && <CheckIcon />}
                            <UIGameImg version={save?.version ?? null} size='1lh' />
                            {save ? <>{name} - {save.trainerName}</> : 'PKVault'}
                        </Group>;
                    }}
                    renderPill={({ value }) => {
                        if (!saveInfosQuery.data || !value)
                            return null;
                        const saveId = +value;
                        const save = saveInfosQuery.data.data[ saveId ];
                        return <UIGameImg version={save?.version ?? null} size='1lh' />;
                    }}
                    searchable
                    comboboxProps={{ withinPortal: false, position: 'left-start', floatingHeight: "viewport" }}
                    floatingHeight="viewport"
                />

                <Alert variant='outline' color='orange' icon={<AlertTriangleIcon />} style={{ whiteSpace: "pre-line" }}>
                    {t("storage.actions.unsafe")}
                </Alert>
            </UIFormCard>
        </Popover.Dropdown>
    </Popover>;
};
