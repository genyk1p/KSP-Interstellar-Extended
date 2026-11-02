# Известные проблемы KSPI-E

Список, чтобы знать, где может подвести. Источники:
1. **Найдено у нас** — в игре со сборкой Kerbalism 3.43 + JNSQ + IFS 3.30 + CryoTanks + BDB (проект ClaudeKSP).
2. **Открытые сообщения об ошибках** у автора: https://github.com/sswelm/KSP-Interstellar-Extended/issues (выгружено 2026-10-31, 58 открытых).

Статусы: **есть** — проблема в коде/конфигах этого форка; **исправлено в форке**; **исправлено у автора** (в master, не в выпуске 1.29.6); **не проверено**; **не наше** — касается мода, которого в нашей сборке нет.

## 1. Найдено у нас

| # | Проблема | Статус |
|---|---|---|
| L1 | **Kerbalism: пустой криобак заставляет кипеть водород в других баках.** Криостат (`FNModuleCryostat`) пустого бака считает себя выключенным и выставляет ресурс кипения на максимум, а процесс Kerbalism «LqdHydrogen Boiloff» списывает водород из общего запаса корабля — из соседнего бака, который охлаждается нормально. Утечка = скорость кипения пустого бака без охлаждения (reserche_long_block_1, CDT2011 пустой, CDT2502 терял ~0,85 л/ч; замер 2026-11-02 через MCP: 0,59 ед./ч, `_LqdHydrogenBoiloff` пустого бака на максимуме 0,164). Тот же механизм у бака, переключённого на другой ресурс в полёте (старое значение кипения оставалось). Корабль, сохранённый с этой ошибкой, течёт и выгруженным, пока его не загрузят новой сборкой. | исправлено в форке (2026-11-02): у пустого бака кипения нет, у переключённого оно обнуляется (`FNModuleCryostat.cs`). Заодно (нашло ревью): охлаждение Kerbalism у пустого бака и бака с выключенным охлаждением оставалось на последнем значении, процесс «Cryo Cooling» тянул электричество (до 12 ед./с на бак CDT2504), — теперь 0 |
| L2 | **Генераторы луча (диодный массив, ЛСЭ, гиротрон) без износа**, и обычный износ Kerbalism к ним не прикрутить: при поломке Kerbalism выключает модуль (`isEnabled = false`), а передатчик (`BeamedPowerTransmitter`) берёт генератор с соседней детали, не проверяя, включён ли он. Луч продолжит работать. Нужна правка кода. | исправлено в форке (2026-10-31): передатчик уважает отказ генератора и в фоне; плюс износ излучателей — `Patches/BeamGeneratorWear.cfg` |
| L3 | **Улучшения диодного массива 4–6** (прикладная/сверхвысокая/экстремальная физика высоких энергий) видны в дереве, но ничего не дают: у `BeamConfiguration` массива заданы только 3 ступени КПД. | есть |
| L4 | **Пустой двигатель на `InterstellarEngineController` запирает газ всего корабля:** при пустом баке каждый шаг физики ставит `mainThrottle = 0`, даже если двигатель выключен (гелиевый `DiatomicMetastableHeliumEngine`, wolf_cree_kerbin). | есть |
| L5 | **Профиль Kerbalism из IFS** (`InterstellarFuelSwitch/Patches/Kerbalsim.cfg`) объявляет ~28 процессов испарения для ресурсов, которых нет → Kerbalism пропускает их с ошибкой в логе. Безвредно. То же, что #694. | есть |
| L6 | **Список деталей для Filter Extensions неполный** (`WarpPlugin/Category/Category.cfg`): ~170 видимых деталей KSPIE в нём нет, часть имён устарела (`FreeElectronLaser` → `BPFEL2`, `DiodeLaserArray` → `BPDLA`, `Gyrotron` → `BPGyrotron`). У нас обойдено своей вкладкой `zzz_MyFilters/20_kspie_category.cfg`. | есть |
| L7 | **Мгновенная наука KSPIE мимо Kerbalism:** эксперименты `ModuleScienceExperiment` на 17 деталях (магнитометр, масс-спектрометры, лазеры, приёмники…) дают 10–200 науки за нажатие. У нас сняты патчем `zzz_KSPIEScience`. | есть |
| L8 | **Принятый PR #692 (эффекты Waterfall для РСУ) не компилировался:** файл `WaterfallIntegration.cs` не вписан в проект, нет ссылки на Waterfall, код под старый интерфейс Waterfall. То есть master с января 2022 никто не собирал. | исправлено в форке |
| L9 | **Конфликт B9PartSwitch с BDB** («Serious Warning» при загрузке): обе стороны управляют Hydrogen/Oxygen на деталях BDB. | есть |

## 2. Открытые сообщения об ошибках — касаются нашей сборки

### Энергия по лучу
| Issue | Проблема | Статус |
|---|---|---|
| [#720](https://github.com/sswelm/KSP-Interstellar-Extended/issues/720) | NullReferenceException в `BeamedPowerReceiver` (`GetVesselPos`) в КЦК и станции слежения — лог раздувается до гигабайт, FPS падает. | автор добавил проверку на null (07cfabc7), полностью не подтверждено |
| [#740](https://github.com/sswelm/KSP-Interstellar-Extended/issues/740) | Абляционное лазерное сопло случайно роняет игру (~80 % запусков на мощном ИК-лазере). | не проверено |
| [#743](https://github.com/sswelm/KSP-Interstellar-Extended/issues/743) | Рентгеновский фотоэлектрический приёмник показывает длину волны «дальний ИК», хотя принимает рентген — только надпись. | есть (косметика) |

### Реакторы и двигатели
| Issue | Проблема | Статус |
|---|---|---|
| [#696](https://github.com/sswelm/KSP-Interstellar-Extended/issues/696) | «Swap fuel» у реактора навсегда удаляет используемое топливо. | не проверено |
| [#678](https://github.com/sswelm/KSP-Interstellar-Extended/issues/678) | При смене топлива у реактора на жидком металле и солевого реактора пропадают баки (EnrU, бериллий). | не проверено |
| [#670](https://github.com/sswelm/KSP-Interstellar-Extended/issues/670) | Солевой реактор: смена топлива в VAB не сохраняется (карьера), проблемы с генератором. | большая часть исправлена в 1.26.23, хвосты не проверены |
| [#685](https://github.com/sswelm/KSP-Interstellar-Extended/issues/685) | Ядерные тепловые двигатели: если уйти в варп во время набора тяги, тяга застывает ниже 100 %. Магнитное сопло: при сбросе газа Isp не растёт, мощность реактора не падает. | не проверено |
| [#714](https://github.com/sswelm/KSP-Interstellar-Extended/issues/714) | S.A.G.E. выключен, но держит реактор на долях процента → утечка антиматерии. | не проверено |
| [#713](https://github.com/sswelm/KSP-Interstellar-Extended/issues/713) | S.A.G.E. / S.A.R.G.E. не летают в атмосфере на тепловом режиме. | не проверено |
| [#717](https://github.com/sswelm/KSP-Interstellar-Extended/issues/717) | H.A.L.B.E.R.D. выключается «по радиационной безопасности»: наследует `lethalDistance = 2000` от термоядерного контроллера. Обход: `@PART[kspiHalberd] { @MODULE[ChemicalEngine] { lethalDistance = 0.0001 } }`. То же у метановых двигателей `FNMethaneEngine`, `FNMethaneEngine2`. | исправлено в форке (2026-11-02): `lethalDistance = 0` в конфигах трёх деталей, как у Rutherford |
| [#697](https://github.com/sswelm/KSP-Interstellar-Extended/issues/697) | Метановый двигатель «Deinonychus» после улучшений даёт гигаватты тепловых потерь (внутри он `VistaECU2`), а тяга не растёт. | не проверено |
| [#710](https://github.com/sswelm/KSP-Interstellar-Extended/issues/710) | Двигатель MIF без факела и звука, пока не изучен узел «Very Heavy Lifter»; в меню «водород», а тратит литий. | не проверено |
| [#711](https://github.com/sswelm/KSP-Interstellar-Extended/issues/711) | Плазменное сопло: «Tweakscale: disabled». | не проверено |
| [#722](https://github.com/sswelm/KSP-Interstellar-Extended/issues/722) | Сверхплотный дейтерий виден в списке режимов реакторов Tri Alpha / MIF, но не выбирается (отключён намеренно, 41a363db). | есть (косметика) |
| [#588](https://github.com/sswelm/KSP-Interstellar-Extended/issues/588) | Ядро антиматерии + магнитное сопло дают ΔV примерно в 11 раз выше заявленного Isp. | не проверено |
| [#610](https://github.com/sswelm/KSP-Interstellar-Extended/issues/610) | РСУ на дуговых двигателях 90° / 45° молчат при тяге строго назад. | не проверено |
| [#709](https://github.com/sswelm/KSP-Interstellar-Extended/issues/709), [#708](https://github.com/sswelm/KSP-Interstellar-Extended/issues/708) | Ионный двигатель «не работает на EC» — так задумано: с KSPIE он берёт мегаджоули. | не баг |
| [#656](https://github.com/sswelm/KSP-Interstellar-Extended/issues/656) | Просьба: показывать уровень улучшения мощности реактора в VAB. | пожелание |

### Баки, IFS, CryoTanks
| Issue | Проблема | Статус |
|---|---|---|
| [#715](https://github.com/sswelm/KSP-Interstellar-Extended/issues/715) | CDT (криогенные двойные баки): вариант «Liquid Water» не создаётся, вода цепляется к LqdAluminium+Oxygen. | не проверено (мы используем CDT2011/2502) |
| [#737](https://github.com/sswelm/KSP-Interstellar-Extended/issues/737), [#668](https://github.com/sswelm/KSP-Interstellar-Extended/issues/668) | Патч KSPIE для баков CryoTanks убирает варианты LH2/Ox (LH2OCryo) и LMOx. | не проверено |
| [#703](https://github.com/sswelm/KSP-Interstellar-Extended/issues/703) | В карьере вариант LH2O виден в подсказке бака, но не выбирается. | не проверено |
| [#712](https://github.com/sswelm/KSP-Interstellar-Extended/issues/712) | В IFS нет варианта LiquidMethane+Oxidizer. | пожелание |
| [#731](https://github.com/sswelm/KSP-Interstellar-Extended/issues/731) | Сухая цена баков после переключения и при масштабировании считается странно. | не проверено |
| [#676](https://github.com/sswelm/KSP-Interstellar-Extended/issues/676) | Нулевая/отрицательная цена у переработчика ядерного топлива ISRU, реактора на антиматерии, сборщика антиматерии IXS. | не проверено |
| [#693](https://github.com/sswelm/KSP-Interstellar-Extended/issues/693) | `TechRequired = Unresearchable` вместо `Unresearcheable` в `IFS/PatchManager/ActiveMMPatches/OnsoleteTanks.cfg` — устаревшие баки не прячутся. | у нас не влияет (проверено 2026-11-02 по кэшу MM): тот же патч ставит `category = none`, все 11 баков скрыты из VAB; в сейве kerbalizm их нет |
| [#575](https://github.com/sswelm/KSP-Interstellar-Extended/issues/575) | С B9PartSwitch у баков пропадают обычные LF/Ox (патч `Patches/B9PartSwitch/CryoTanksFuelSwitcher.cfg`). | не проверено |

### ISRU и ресурсы
| Issue | Проблема | Статус |
|---|---|---|
| [#724](https://github.com/sswelm/KSP-Interstellar-Extended/issues/724) | «Пиролиз метана» (CH4 → H2 + C) на деле делает частичное окисление (CH4 + O2 → CO + H2): классы одинаковые. Ещё у обоих не сходилась масса: продукты — 0,76 от сырья, метан:кислород 20:32 вместо 32:32. Стоит на `KSPI_ISRU_Processor` и `_Large`. | исправлено в форке (2026-11-02): пиролиз CH4 → C + 2H2 без кислорода, углерод — в ресурс CRP `Carbon` (нужно место или разрешённый перелив, как у других продуктов); частичное окисление 2CH4 + O2 → 2CO + 4H2 с верными долями. В игре не проверено: нет корабля с переработчиком |
| [#716](https://github.com/sswelm/KSP-Interstellar-Extended/issues/716) | Тепловая струя: при выборе воды показывает угарный газ. Рефрижератор не сжижает аргон. | не проверено |
| [#732](https://github.com/sswelm/KSP-Interstellar-Extended/issues/732) | Атмосферный сборщик частиц собирает в основном жидкости — в описании не сказано. | есть (описание) |
| [#260](https://github.com/sswelm/KSP-Interstellar-Extended/issues/260) | Ресурсы Borate, Fluorite, Nitratine, Salt не из CRP; SCANsat не видит Nitratine, Fluorite, Salt, Spodumene, Regolith. | не проверено |
| [#723](https://github.com/sswelm/KSP-Interstellar-Extended/issues/723) | Просьба: синтез воды в ISRU. | пожелание |

### Конфиги и совместимость
| Issue | Проблема | Статус |
|---|---|---|
| [#705](https://github.com/sswelm/KSP-Interstellar-Extended/issues/705) | Патч солнечных панелей (`Patches/SolarPanels.cfg`) обнуляет EC у деталей, где есть и панель, и батарея (BDB Vanguard 1). | есть (патч тот же), на наших деталях не проверено |
| [#648](https://github.com/sswelm/KSP-Interstellar-Extended/issues/648) | Патчи KSPIE используют `:FOR[ЧужойМод]` — это создаёт проход Module Manager, и `NEEDS[ЧужойМод]` у других модов срабатывает, будто мод установлен. Нужно `BEFORE`/`AFTER`. | не проверено, что осталось |
| [#729](https://github.com/sswelm/KSP-Interstellar-Extended/issues/729) | Загрузка зависает, если в дереве технологий есть узел без `id`. | есть (у нас все узлы с id) |
| [#694](https://github.com/sswelm/KSP-Interstellar-Extended/issues/694) | Ошибки профиля Kerbalism из IFS — см. L5. | есть |
| [#681](https://github.com/sswelm/KSP-Interstellar-Extended/issues/681) | Система терморегуляции раскрывается сразу после старта — переключатель «Automated» на радиаторе. | не баг (настройка) |
| [#672](https://github.com/sswelm/KSP-Interstellar-Extended/issues/672), [#688](https://github.com/sswelm/KSP-Interstellar-Extended/issues/688) | Загрузка висит на `activeCoolingSystemv3`, если нет Community Resource Pack. | не наше (CRP стоит) |
| [#677](https://github.com/sswelm/KSP-Interstellar-Extended/issues/677) | Kerbstein Mount: ошибка запятой в рецепте термоядерных таблеток. | исправлено у автора (в конфиге 0.805024) |
| [#671](https://github.com/sswelm/KSP-Interstellar-Extended/issues/671) | Пропал реактор IXS. | исправлено у автора (`IXSMainHullReactor` есть) |
| [#718](https://github.com/sswelm/KSP-Interstellar-Extended/issues/718) | «Улучшения не работают, всё Mk1» — без подробностей. | не проверено |
| [#719](https://github.com/sswelm/KSP-Interstellar-Extended/issues/719) | Анимация сопла ядерного турбореактивного двигателя (только скриншот). | не проверено |
| [#738](https://github.com/sswelm/KSP-Interstellar-Extended/issues/738), [#659](https://github.com/sswelm/KSP-Interstellar-Extended/issues/659) | Китайский перевод устарел; вопрос, что переводить. | не наше |

## 3. Открытые сообщения — не наша сборка

| Issue | О чём |
|---|---|
| [#742](https://github.com/sswelm/KSP-Interstellar-Extended/issues/742) | H.A.L.B.E.R.D. падает с модом Ignition (нет у нас). |
| [#739](https://github.com/sswelm/KSP-Interstellar-Extended/issues/739) | Boring Crew Services: служебный модуль сам отделяется на старте. |
| [#736](https://github.com/sswelm/KSP-Interstellar-Extended/issues/736), [#725](https://github.com/sswelm/KSP-Interstellar-Extended/issues/725) | Procedural Parts: два меню топлива, ёмкость ксенонового бака по умолчанию. |
| [#730](https://github.com/sswelm/KSP-Interstellar-Extended/issues/730) | CKAN: архив 1.29.6 не той длины (2023). |
| [#726](https://github.com/sswelm/KSP-Interstellar-Extended/issues/726), [#675](https://github.com/sswelm/KSP-Interstellar-Extended/issues/675) | Детали не грузятся / 60 ошибок IFS — кривая установка. |
| [#735](https://github.com/sswelm/KSP-Interstellar-Extended/issues/735), [#733](https://github.com/sswelm/KSP-Interstellar-Extended/issues/733), [#727](https://github.com/sswelm/KSP-Interstellar-Extended/issues/727), [#707](https://github.com/sswelm/KSP-Interstellar-Extended/issues/707), [#706](https://github.com/sswelm/KSP-Interstellar-Extended/issues/706) | Вопросы и пожелания (энергия по лучу отдельно, ссылка на Discord, линзирование и звук варп-двигателя). |
