import { Group, GroupProps, Title } from '@mantine/core'
import { forwardRef } from 'react'
import LogoBox from '@Components/LogoBox'
import { useConfig } from '@Utils/useConfig'
import classes from '@Styles/LogoHeader.module.css'

export const LogoHeader = forwardRef<HTMLDivElement, GroupProps>((props, ref) => {
  const { config } = useConfig()
  return (
    <Group ref={ref} wrap="nowrap" {...props}>
      <LogoBox size="60px" />
      <Title className={classes.title}>
        {config?.title ?? 'GZ'}
        <span className={classes.brand}>::</span>CTF
      </Title>
    </Group>
  )
})

export default LogoHeader
